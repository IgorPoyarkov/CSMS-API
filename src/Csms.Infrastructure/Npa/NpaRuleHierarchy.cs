using System.Globalization;
using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Протяжка правил по дереву ОКПД2. Если акт включает класс 26, то все
/// подкоды 26.20.11.110 получают то же правило, пока для них нет своего.
/// Прямое правило всегда важнее унаследованного, а из предков берётся
/// ближайший — так «26 включён, кроме 26.60» работает и для 26.60.11.110.
/// </summary>
public static class NpaRuleHierarchy
{
    /// <summary>Унаследованное правило чуть менее надёжно, чем исходная строка.</summary>
    private const double InheritedConfidenceFactor = 0.95;

    public static IReadOnlyList<NpaRuleImportItem> Propagate(
        IReadOnlyList<NpaRuleImportItem> rules,
        Okpd2Catalog catalog)
    {
        if (catalog.IsEmpty || rules.Count == 0)
        {
            return rules;
        }

        var direct = new Dictionary<string, NpaRuleImportItem>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!rule.IsInherited)
            {
                direct[rule.Okpd2Code] = rule;
            }
        }

        var result = new List<NpaRuleImportItem>(rules);
        var seen = new HashSet<string>(
            rules.Where(rule => rule.IsInherited).Select(rule => rule.Okpd2Code),
            StringComparer.Ordinal);

        foreach (var code in catalog.Codes)
        {
            if (direct.ContainsKey(code) || !seen.Add(code))
            {
                continue;
            }

            foreach (var ancestor in Ancestors(code))
            {
                if (!direct.TryGetValue(ancestor, out var rule))
                {
                    continue;
                }

                result.Add(rule with
                {
                    Okpd2Code = code,
                    IsInherited = true,
                    InheritedFrom = ancestor,
                    Confidence = Math.Round(rule.Confidence * InheritedConfidenceFactor, 3),
                    Note = $"унаследовано от {ancestor}",
                });

                break;
            }
        }

        return result
            .OrderBy(rule => rule.Okpd2Code, StringComparer.Ordinal)
            .ThenBy(rule => rule.IsInherited)
            .ToArray();
    }

    /// <summary>Предки кода от ближайшего к корню: 26.20.11.110 → 26.20.11, 26.20, 26.</summary>
    internal static IEnumerable<string> Ancestors(string code)
    {
        var current = code;
        while (true)
        {
            var parent = NpaRuleSets.ParentOf(current);
            if (parent is null)
            {
                yield break;
            }

            yield return parent;
            current = parent;
        }
    }

    /// <summary>
    /// Действующее правило для кода: самое длинное совпадение по префиксу,
    /// прямое важнее унаследованного при равной длине.
    /// </summary>
    public static NpaRuleInfo? Resolve(IReadOnlyList<NpaRuleInfo> rules, string okpd2Code)
    {
        var code = NpaRuleSets.NormalizeCode(okpd2Code);
        NpaRuleInfo? best = null;

        foreach (var rule in rules)
        {
            var matches = rule.Okpd2Code == code || NpaRuleSets.IsUnder(code, rule.Okpd2Code);
            if (!matches)
            {
                continue;
            }

            if (best is null ||
                rule.Okpd2Code.Length > best.Okpd2Code.Length ||
                (rule.Okpd2Code.Length == best.Okpd2Code.Length && best.IsInherited && !rule.IsInherited))
            {
                best = rule;
            }
        }

        return best;
    }

    internal static string FormatConfidence(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
