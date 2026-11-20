using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Reduce-шаг. Дедупликация — работа кода: LLM не решает, какие пункты
/// «одинаковые». При склейке сохраняются все ссылки на исходные пункты.
/// </summary>
public sealed class DocumentFunctionReducer
{
    public sealed record Result(
        IReadOnlyList<DocumentFunctionItem> Functions,
        DocumentExtractionReport Report);

    public Result Reduce(
        IReadOnlyList<DocumentSegment> sourceSegments,
        IReadOnlyList<DocumentSegmentExtraction> validated)
    {
        var byId = validated.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var uncovered = sourceSegments
            .Where(segment => !byId.ContainsKey(segment.SegmentId))
            .Select(segment => segment.SegmentId)
            .ToList();

        var candidates = new List<Candidate>();
        var quotesRejected = validated.Sum(item => item.Report.QuotesRejected);

        foreach (var extraction in validated)
        {
            foreach (var function in extraction.ExtractedFunctions)
            {
                foreach (var subtask in function.Subtasks)
                {
                    candidates.Add(new Candidate(
                        extraction.SourcePath,
                        function.ParentFunctionTitle,
                        function.Category,
                        function.SuggestedCategory,
                        function.ActorRole,
                        function.Obligation,
                        subtask));
                }
            }
        }

        var groups = candidates
            .GroupBy(candidate => string.Join('|',
                DocumentFunctionNormalizer.ToInfinitive(candidate.Subtask.ActionVerb),
                DocumentFunctionNormalizer.Object(candidate.Subtask.TargetObject),
                DocumentFunctionNormalizer.Compare(candidate.ActorRole ?? string.Empty),
                candidate.Obligation,
                DocumentFunctionNormalizer.Compare(candidate.Subtask.TriggerCondition ?? string.Empty)),
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var functions = new List<DocumentFunctionItem>();
        foreach (var group in groups)
        {
            var first = group.First();
            var category = group
                .GroupBy(candidate => candidate.Category)
                .OrderByDescending(part => part.Count())
                .ThenBy(part => part.Key, StringComparer.Ordinal)
                .First().Key;
            var titles = group
                .Select(candidate => candidate.ParentFunctionTitle)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var paths = group
                .Select(candidate => candidate.SourcePath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var subtasks = new List<DocumentFunctionSubtaskItem>();
            foreach (var subtaskGroup in group.GroupBy(candidate => string.Join('|',
                         DocumentFunctionNormalizer.ToInfinitive(candidate.Subtask.ActionVerb),
                         DocumentFunctionNormalizer.Object(candidate.Subtask.TargetObject),
                         DocumentFunctionNormalizer.Compare(candidate.Subtask.SourceQuote)),
                         StringComparer.Ordinal))
            {
                var sample = subtaskGroup.First().Subtask;
                subtasks.Add(new DocumentFunctionSubtaskItem(
                    Guid.NewGuid(),
                    subtaskGroup.Key,
                    sample.ActionVerb,
                    sample.TargetObject,
                    sample.Description,
                    sample.TriggerCondition,
                    sample.DeadlineRule,
                    sample.SourceQuote,
                    subtaskGroup.Select(candidate => candidate.SourcePath).Distinct().ToArray(),
                    subtaskGroup.Any(candidate => candidate.Subtask.NeedsReview),
                    subtaskGroup.Max(candidate => candidate.Subtask.Confidence)));
            }

            functions.Add(new DocumentFunctionItem(
                Guid.NewGuid(),
                group.Key,
                titles.Length == 0 ? "Функция" : string.Join(" / ", titles),
                category,
                group.Select(candidate => candidate.SuggestedCategory)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                group.Select(candidate => candidate.ActorRole)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                first.Obligation,
                group.Count() == 1 ? "один сегмент" : "совпали глагол, объект, роль и условие",
                paths,
                subtasks,
                DocumentFunctionReviewStatuses.NeedsReview,
                null));
        }

        var report = new DocumentExtractionReport(
            sourceSegments.Count,
            validated.Count,
            [],
            uncovered,
            candidates.Count,
            functions.Count,
            candidates.Count - functions.Count,
            functions.Sum(function => function.Subtasks.Count(subtask => subtask.NeedsReview)),
            quotesRejected);

        return new Result(functions, report);
    }

    private sealed record Candidate(
        string SourcePath,
        string ParentFunctionTitle,
        string Category,
        string? SuggestedCategory,
        string? ActorRole,
        string Obligation,
        ExtractedDocumentSubtask Subtask);
}
