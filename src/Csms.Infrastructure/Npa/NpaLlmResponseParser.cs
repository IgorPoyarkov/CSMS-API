using System.Text.Json;
using System.Text.Json.Serialization;
using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Разбор и проверка ответа LLM-аналитика. Вынесено из клиента отдельно, чтобы
/// тестировать защиту от галлюцинаций без обращения к модели.
///
/// Два фильтра обязательны: код должен быть среди кандидатов справочника, а
/// цитата — дословно присутствовать в строке акта. Всё остальное модель может
/// напридумывать, и в правила оно не попадает.
/// </summary>
public static class NpaLlmResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Модель иногда оборачивает JSON в текст несмотря на json_object — пробуем
    /// вырезать объект по первой и последней фигурной скобке.
    /// </summary>
    public static LlmResponse? Parse(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<LlmResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<LlmResponse>(
                    content[start..(end + 1)], JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Принимает только безопасные предложения: код из списка кандидатов,
    /// цитата из исходной строки, известный тип совпадения. Количество
    /// отклонённых позиций возвращается вместе с принятыми.
    /// </summary>
    public static (IReadOnlyList<NpaLlmSuggestion> Accepted, int Rejected) Validate(
        LlmResponse response,
        IReadOnlyList<NpaLlmInputRow> batch)
    {
        var results = response.Results ?? [];
        var accepted = new List<NpaLlmSuggestion>();
        var rejected = 0;
        var seenRows = new HashSet<NpaLlmInputRow>(ReferenceEqualityComparer.Instance);

        foreach (var item in results)
        {
            var matchType = (item.MatchType ?? string.Empty).Trim().ToLowerInvariant();
            if (matchType is "not_found" || string.IsNullOrWhiteSpace(item.Okpd2Code))
            {
                continue;
            }

            if (matchType is not ("explicit" or "implicit" or "excluded"))
            {
                rejected++;
                continue;
            }

            var code = NpaRuleSets.NormalizeCode(item.Okpd2Code);
            var quote = item.SourceQuote ?? string.Empty;
            var row = batch.FirstOrDefault(candidate =>
                ContainsQuote(candidate.Text, quote));

            // Код обязан быть кандидатом именно этой строки: иначе модель
            // переносит код из соседней позиции батча на чужую цитату.
            if (row is null || quote.Length < 5 || !row.Candidates.Any(candidate =>
                    string.Equals(
                        NpaRuleSets.NormalizeCode(candidate.Code),
                        code,
                        StringComparison.Ordinal)))
            {
                rejected++;
                continue;
            }

            // Маленькая модель часто выдаёт по несколько результатов на строку.
            // Оставляем первый, остальные не попадают в правила.
            if (!seenRows.Add(row))
            {
                rejected++;
                continue;
            }

            // explicit с flag_active=false — то же исключение, что и «кроме».
            var ruleType = matchType switch
            {
                "excluded" => NpaRuleTypes.Exclude,
                "implicit" => NpaRuleTypes.Conditional,
                _ when item.FlagActive == false => NpaRuleTypes.Exclude,
                _ => NpaRuleTypes.Include,
            };

            var conditions = (item.Conditions ?? [])
                .Where(condition => !string.IsNullOrWhiteSpace(condition))
                .Select(condition => condition.Trim())
                .ToArray();

            accepted.Add(new NpaLlmSuggestion(
                code,
                item.ItemName ?? string.Empty,
                matchType,
                ruleType,
                conditions,
                quote,
                matchType == "implicit" || item.RequiresHumanReview,
                row.Page,
                row.RowIndex));
        }

        return (accepted, rejected);
    }

    /// <summary>Сравнение цитаты с нормализацией пробелов и регистра.</summary>
    internal static bool ContainsQuote(string text, string quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return false;
        }

        static string Normalize(string value) =>
            string.Join(' ', value.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        return Normalize(text).Contains(Normalize(quote), StringComparison.Ordinal);
    }

    public sealed class LlmResponse
    {
        [JsonPropertyName("act_info")]
        public LlmActInfo? ActInfo { get; set; }

        [JsonPropertyName("results")]
        public List<LlmResultItem>? Results { get; set; }
    }

    public sealed class LlmActInfo
    {
        [JsonPropertyName("requisites")]
        public string? Requisites { get; set; }

        [JsonPropertyName("is_framework_act")]
        public bool IsFrameworkAct { get; set; }
    }

    public sealed class LlmResultItem
    {
        [JsonPropertyName("okpd2_code")]
        public string? Okpd2Code { get; set; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; set; }

        [JsonPropertyName("match_type")]
        public string? MatchType { get; set; }

        [JsonPropertyName("flag_active")]
        public bool? FlagActive { get; set; }

        [JsonPropertyName("has_conditions")]
        public bool HasConditions { get; set; }

        [JsonPropertyName("conditions")]
        public List<string>? Conditions { get; set; }

        [JsonPropertyName("source_quote")]
        public string? SourceQuote { get; set; }

        [JsonPropertyName("requires_human_review")]
        public bool RequiresHumanReview { get; set; }
    }
}
