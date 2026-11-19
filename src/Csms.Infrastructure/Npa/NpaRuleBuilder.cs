using System.Text.RegularExpressions;
using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Собирает правила из разобранного акта. Явные коды и оговорки «кроме»
/// применяются автоматически; позиции без кода разбираются через справочник
/// ОКПД2 по наименованию и обязательно уходят в очередь на проверку.
/// </summary>
public static partial class NpaRuleBuilder
{
    private const double NameMatchThreshold = 0.75;

    // Закрывающая скобка необязательна: OCR часто обрывает длинную оговорку
    // на конце строки, но сами коды из текста уже доступны.
    [GeneratedRegex(@"(?:\(\s*)?кроме\s+кодов?\s*(?<codes>[^)]*)\)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NestedExclusionRegex();

    // OCR переносит длинный перечень «кроме кодов …» на следующую строку.
    // Тогда код из продолжения оказывается в начале наименования следующей строки.
    [GeneratedRegex(@"^(?<codes>[0-9][0-9.,;\s)]*)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCodesRegex();

    // Неразобранная строка OCR может быть обрывком «кроме кодов …»: в ней
    // остались только коды с точками. Одиночное число без точки — дата или
    // номер пункта — сюда не попадает.
    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex DottedCodeRegex();

    // «Кроме танкеров», «кроме машин» — уточнение наименования позиции, а не
    // исключение кода. Исключением считаем только оговорку про коды: между
    // «кроме» и «код» могут оказаться 1–3 слова OCR вроде «оптическое».
    [GeneratedRegex(@"кроме\s+(?:\S+\s+){0,3}?код(?:а|ы|ов|ом)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeCaveatRegex();

    public static IReadOnlyList<NpaRuleImportItem> Build(
        NpaParsedDocument parsed,
        double minAutoConfidence,
        Okpd2Catalog catalog)
    {
        var byCode = new Dictionary<string, NpaRuleImportItem>(StringComparer.Ordinal);
        var resolver = new Okpd2CodeResolver(catalog);

        foreach (var row in parsed.Rows)
        {
            // Цитата нужна для аудита, но не для решения: в разложке она часто
            // повторяет наименование и захватывает соседние строки.
            var decision = NpaRuleClassifier.Decide(
                row.Name,
                null,
                row.CodeConfidence,
                minAutoConfidence);

            byCode[row.Code] = new NpaRuleImportItem(
                row.Code,
                decision.RuleType,
                decision.ConditionText,
                row.Quote,
                IsInherited: false,
                InheritedFrom: null,
                decision.Confidence,
                decision.Status,
                decision.Note,
                row.Page,
                row.RowIndex);

            AddNestedExclusions(row, resolver, byCode);
            AddLeadingExclusions(row, resolver, byCode);
        }

        // Оговорка «кроме ...» сильнее строки таблицы: именно она выводит код.
        foreach (var exclusion in parsed.Exclusions)
        {
            var decision = NpaRuleClassifier.Decide(null, exclusion.Quote, 0.9, minAutoConfidence);

            byCode[exclusion.Code] = new NpaRuleImportItem(
                exclusion.Code,
                NpaRuleTypes.Exclude,
                exclusion.Quote,
                exclusion.Quote,
                IsInherited: false,
                InheritedFrom: null,
                0.9,
                NpaRuleStatuses.Auto,
                "явная оговорка в тексте акта",
                exclusion.Page,
                null);
        }

        // Явные коды из строк, которые разложка не привязала к колонке. Идут
        // до подбора по наименованию, чтобы код не стал conditional.
        AddUnresolvedCodes(parsed, resolver, byCode);

        foreach (var unresolved in parsed.Unresolved)
        {
            var matches = catalog.SearchByName(unresolved.Text, 1);
            if (matches.Count == 0 || matches[0].Score < NameMatchThreshold)
            {
                continue;
            }

            var (code, name, score) = matches[0];
            if (byCode.ContainsKey(code))
            {
                continue;
            }

            byCode[code] = new NpaRuleImportItem(
                code,
                NpaRuleTypes.Conditional,
                $"код подобран по наименованию «{name}»",
                unresolved.Text,
                IsInherited: false,
                InheritedFrom: null,
                Math.Min(score, 0.7),
                NpaRuleStatuses.NeedsReview,
                "код в акте не указан, подобран по справочнику ОКПД2",
                unresolved.Page,
                unresolved.RowIndex);
        }

        return byCode.Values
            .OrderBy(rule => rule.Okpd2Code, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Явные коды в неразобранных строках OCR. Обрывок оговорки про коды или
    /// минимум два кода с точками дают `exclude`; одиночный код даёт `include`
    /// с `needs_review`, потому что разложка не подтвердила его колонку.
    /// Даты, номера пунктов и «кроме танкеров» в правила не попадают.
    /// </summary>
    private static void AddUnresolvedCodes(
        NpaParsedDocument parsed,
        Okpd2CodeResolver resolver,
        Dictionary<string, NpaRuleImportItem> byCode)
    {
        foreach (var unresolved in parsed.Unresolved)
        {
            var text = unresolved.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var tokens = DottedCodeRegex().Matches(text)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (tokens.Length == 0)
            {
                continue;
            }

            var isCodeCaveat = CodeCaveatRegex().IsMatch(text);
            var isExclusion = isCodeCaveat || tokens.Length >= 2;

            foreach (var token in tokens)
            {
                var resolution = resolver.Resolve(token);
                if (resolution is null)
                {
                    continue;
                }

                if (!isExclusion && byCode.ContainsKey(resolution.Code))
                {
                    continue;
                }

                byCode[resolution.Code] = new NpaRuleImportItem(
                    resolution.Code,
                    isExclusion ? NpaRuleTypes.Exclude : NpaRuleTypes.Include,
                    text,
                    text,
                    IsInherited: false,
                    InheritedFrom: null,
                    isExclusion ? 0.9 : 0.75,
                    isExclusion ? NpaRuleStatuses.Auto : NpaRuleStatuses.NeedsReview,
                    isExclusion
                        ? "код из неразобранной оговорки акта"
                        : "явный код в неразобранной строке акта",
                    unresolved.Page,
                    unresolved.RowIndex);
            }
        }
    }

    /// <summary>
    /// Перенос оговорки «кроме кодов 25.30.2, 25.71.14.120, 25.40.1)» на
    /// следующую строку таблицы: коды из начала наименования тоже исключения.
    /// Посторонние числа отсекаются сверкой со справочником ОКПД2.
    /// </summary>
    private static void AddLeadingExclusions(
        NpaTableRow row,
        Okpd2CodeResolver resolver,
        Dictionary<string, NpaRuleImportItem> byCode)
    {
        var text = row.Quote ?? row.Name;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = LeadingCodesRegex().Match(text);
        if (!match.Success)
        {
            return;
        }

        var quote = match.Value.Trim();
        foreach (var token in quote.Split(
                     [',', ';', ')', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var resolution = resolver.Resolve(token);
            if (resolution is null)
            {
                continue;
            }

            byCode[resolution.Code] = new NpaRuleImportItem(
                resolution.Code,
                NpaRuleTypes.Exclude,
                quote,
                quote,
                IsInherited: false,
                InheritedFrom: null,
                0.9,
                NpaRuleStatuses.Auto,
                "продолжение оговорки строки акта",
                row.Page,
                null);
        }
    }

    /// <summary>
    /// Оговорка «(кроме кодов 23.13.13.140, …)» относится к строке таблицы, но
    /// задаёт отдельные коды, которые из перечня выведены. Разбираем их явно и
    /// не ждём, что OCR случайно отнесёт их к колонке кодов.
    /// </summary>
    private static void AddNestedExclusions(
        NpaTableRow row,
        Okpd2CodeResolver resolver,
        Dictionary<string, NpaRuleImportItem> byCode)
    {
        var text = row.Quote ?? row.Name;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = NestedExclusionRegex().Match(text);
        if (!match.Success)
        {
            return;
        }

        var quote = match.Value;
        foreach (var token in match.Groups["codes"].Value.Split(
                     [',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var resolution = resolver.Resolve(token);
            if (resolution is null)
            {
                continue;
            }

            byCode[resolution.Code] = new NpaRuleImportItem(
                resolution.Code,
                NpaRuleTypes.Exclude,
                quote,
                quote,
                IsInherited: false,
                InheritedFrom: null,
                0.9,
                NpaRuleStatuses.Auto,
                "явный код в оговорке строки акта",
                row.Page,
                null);
        }
    }
}
