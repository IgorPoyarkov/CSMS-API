using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Validate-шаг. Доверяем модели только формулировки, но не факты: цитата
/// обязана быть в тексте сегмента, а срок, роль и условие — в цитате или в
/// заголовках-предках. Всё остальное помечается needs_review, а не публикуется.
/// </summary>
public sealed class DocumentFunctionValidator
{
    public sealed record Result(DocumentSegmentExtraction Extraction, int QuotesRejected);

    public Result Validate(DocumentSegmentExtraction extraction, DocumentSegment segment)
    {
        var context = string.Join(
            "\n",
            segment.Ancestors.Select(ancestor => ancestor.Text).Append(segment.Text));
        var functions = new List<ExtractedDocumentFunction>();
        var rejected = 0;
        var needsReview = 0;

        foreach (var function in extraction.ExtractedFunctions)
        {
            var subtasks = new List<ExtractedDocumentSubtask>();
            foreach (var subtask in function.Subtasks)
            {
                var quote = DocumentFunctionNormalizer.Whitespace(subtask.SourceQuote);
                if (quote.Length == 0 ||
                    !DocumentFunctionNormalizer.ContainsNormalized(segment.Text, quote))
                {
                    // Галлюцинированную цитату нельзя оставлять даже на проверке.
                    rejected++;
                    continue;
                }

                var flag = subtask.NeedsReview;
                var deadline = Grounded(subtask.DeadlineRule, context, ref flag);
                var trigger = Grounded(subtask.TriggerCondition, context, ref flag);

                var rawVerb = subtask.ActionVerb;
                var verb = DocumentFunctionNormalizer.ToInfinitive(rawVerb);
                var target = DocumentFunctionNormalizer.Object(subtask.TargetObject);
                // Инфинитив «разработать» не является подстрокой «разрабатывает»,
                // поэтому сверяем цитату и с исходной формой глагола тоже.
                var verbInQuote =
                    DocumentFunctionNormalizer.ContainsNormalized(quote, rawVerb) ||
                    DocumentFunctionNormalizer.ContainsNormalized(quote, verb);
                if (verb.Length == 0 || target.Length == 0 ||
                    !verbInQuote ||
                    !DocumentFunctionNormalizer.ContainsNormalized(quote, target))
                {
                    flag = true;
                }

                if (subtask.Confidence <= 0)
                    flag = true;

                if (flag)
                    needsReview++;

                subtasks.Add(subtask with
                {
                    ActionVerb = verb,
                    TargetObject = target,
                    DeadlineRule = deadline,
                    TriggerCondition = trigger,
                    NeedsReview = flag,
                    SourceQuote = quote,
                });
            }

            if (subtasks.Count == 0)
                continue;

            var functionNeedsReview = false;
            var actor = Grounded(function.ActorRole, context, ref functionNeedsReview);
            if (functionNeedsReview)
                needsReview += subtasks.Count;
            var category = function.Category;
            var suggested = function.SuggestedCategory;
            if (!DocumentFunctionTaxonomy.Codes.Contains(category, StringComparer.Ordinal))
            {
                category = DocumentFunctionTaxonomy.Other;
                suggested ??= function.Category;
                needsReview += subtasks.Count;
            }

            if (category == DocumentFunctionTaxonomy.Other && string.IsNullOrWhiteSpace(suggested))
            {
                needsReview += subtasks.Count;
            }

            functions.Add(function with
            {
                Category = category,
                SuggestedCategory = suggested,
                ActorRole = actor,
                Subtasks = subtasks,
            });
        }

        var status = functions.Count == 0 ? "informational" : extraction.Status;
        var validated = extraction with
        {
            Status = status,
            ExtractedFunctions = functions,
            Report = extraction.Report with
            {
                SegmentsWithFunctions = functions.Count == 0 ? 0 : 1,
                Informational = functions.Count == 0,
                Candidates = functions.Sum(function => function.Subtasks.Count),
                QuotesRejected = rejected,
                NeedsReview = needsReview,
            },
        };

        return new Result(validated, rejected);
    }

    private static string? Grounded(string? value, string context, ref bool needsReview)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = DocumentFunctionNormalizer.Whitespace(value);
        if (DocumentFunctionNormalizer.ContainsNormalized(context, normalized))
            return normalized;

        needsReview = true;
        return null;
    }
}
