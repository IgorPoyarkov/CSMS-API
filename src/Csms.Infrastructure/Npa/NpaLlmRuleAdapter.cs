using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Мост между спорными строками детерминированного разбора и LLM-аналитиком.
/// Готовит только те строки, где есть кандидаты из справочника, и не даёт
/// модели выбирать код вне активного снимка ОКПД2.
/// </summary>
public static class NpaLlmRuleAdapter
{
    /// <summary>Уверенность предложений модели: ниже порога авто-применения.</summary>
    private const double SuggestionConfidence = 0.55;

    public static NpaLlmAnalysisRequest BuildRequest(
        string code,
        string title,
        string? number,
        IReadOnlyList<NpaUnresolvedRow> rows,
        Okpd2Catalog catalog,
        int maxCandidates)
    {
        var input = new List<NpaLlmInputRow>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Text))
            {
                continue;
            }

            var candidates = catalog.SearchByName(row.Text, maxCandidates)
                .Select(candidate => new NpaLlmCandidate(candidate.Code, candidate.Name))
                .ToArray();
            if (candidates.Length == 0)
            {
                // Модель не может выбрать код из пустого списка — это ровно тот
                // случай, когда запрещено галлюцинировать ОКПД2.
                continue;
            }

            input.Add(new NpaLlmInputRow(row.Text, row.Page, row.RowIndex, candidates));
        }

        return new NpaLlmAnalysisRequest(code, title, number, input);
    }

    /// <summary>
    /// Предложения модели всегда идут в очередь проверки: человек подтверждает
    /// или отклоняет их тем же endpoint'ом /verify, что и правила первого агента.
    /// </summary>
    public static IReadOnlyList<NpaRuleImportItem> ToRules(
        NpaLlmAnalysisResult analysis,
        string model)
    {
        return analysis.Suggestions
            .Select(suggestion => new NpaRuleImportItem(
                suggestion.Okpd2Code,
                suggestion.RuleType,
                suggestion.Conditions.Count == 0
                    ? null
                    : string.Join("; ", suggestion.Conditions),
                suggestion.SourceQuote,
                IsInherited: false,
                InheritedFrom: null,
                SuggestionConfidence,
                NpaRuleStatuses.NeedsReview,
                $"LLM {model}: {suggestion.MatchType}; {suggestion.ItemName}",
                suggestion.Page,
                suggestion.RowIndex))
            .ToArray();
    }
}
