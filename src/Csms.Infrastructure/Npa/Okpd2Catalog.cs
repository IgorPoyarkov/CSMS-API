using System.Globalization;
using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Снимок действующего справочника ОКПД2 в памяти. Агент разметки сверяет с
/// ним каждое число, распознанное на странице: код, которого нет в справочнике,
/// не может быть кодом ОКПД2 — это шум OCR (номера пунктов, сноски, суммы).
/// </summary>
public sealed class Okpd2Catalog
{
    private static readonly char[] TokenSeparators = [' ', ',', '.', ';', ':', '(', ')', '«', '»', '"', '\'', '-', '/', '№'];

    private readonly Dictionary<string, string> _names;
    private readonly Dictionary<string, List<string>> _byDigits;
    private readonly List<(string Code, string[] Tokens)> _byTokens;

    private Okpd2Catalog(
        Dictionary<string, string> names,
        Dictionary<string, List<string>> byDigits,
        List<(string Code, string[] Tokens)> byTokens)
    {
        _names = names;
        _byDigits = byDigits;
        _byTokens = byTokens;
    }

    /// <summary>Пустой справочник: проверки по кодам отключаются.</summary>
    public static Okpd2Catalog Empty { get; } = new([], [], []);

    public int Count => _names.Count;

    public bool IsEmpty => _names.Count == 0;

    /// <summary>Все коды действующего справочника.</summary>
    public IEnumerable<string> Codes => _names.Keys;

    public static Okpd2Catalog FromItems(IEnumerable<ReferenceItemInfo> items)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var byDigits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byTokens = new List<(string Code, string[] Tokens)>();

        foreach (var item in items)
        {
            var code = NpaRuleSets.NormalizeCode(item.Code);
            // В выгрузке НСИ встречаются нецифровые «коды» других
            // классификаторов. В ОКПД2 только цифры и точки — всё остальное
            // не должно становиться правилом разметки.
            if (!IsNumericCode(code) || !names.TryAdd(code, item.Name ?? string.Empty))
            {
                continue;
            }

            var digits = code.Replace(".", string.Empty, StringComparison.Ordinal);
            if (!byDigits.TryGetValue(digits, out var codes))
            {
                codes = [];
                byDigits[digits] = codes;
            }

            codes.Add(code);

            var tokens = Tokenize(item.Name);
            if (tokens.Length > 0)
            {
                byTokens.Add((code, tokens));
            }
        }

        return new Okpd2Catalog(names, byDigits, byTokens);
    }

    public bool Contains(string code) => _names.ContainsKey(code);

    public string? NameOf(string code) => _names.GetValueOrDefault(code);

    /// <summary>
    /// Код по строке цифр без разделителей. Нужен, когда OCR потерял точки:
    /// «262011110» → 26.20.11.110. Неоднозначные совпадения не принимаются.
    /// </summary>
    public string? CodeOfDigits(string digits)
    {
        if (!_byDigits.TryGetValue(digits, out var codes))
        {
            return null;
        }

        return codes.Count == 1 ? codes[0] : null;
    }

    /// <summary>
    /// Подбор кодов по наименованию товара — для строк, где код в акте не указан.
    /// Сравнение по словам, без семантики: результат всегда идёт на проверку.
    /// </summary>
    public IReadOnlyList<(string Code, string Name, double Score)> SearchByName(string text, int take = 3)
    {
        var tokens = Tokenize(text);
        if (tokens.Length == 0 || _byTokens.Count == 0)
        {
            return [];
        }

        var needle = tokens.ToHashSet(StringComparer.Ordinal);
        var scored = new List<(string Code, string Name, double Score)>();

        foreach (var (code, candidate) in _byTokens)
        {
            var hits = candidate.Count(needle.Contains);
            if (hits == 0)
            {
                continue;
            }

            // Симметричная мера: совпадение должно покрывать и запрос, и название.
            var score = 2.0 * hits / (needle.Count + candidate.Length);
            if (score >= 0.5)
            {
                scored.Add((code, _names.GetValueOrDefault(code) ?? string.Empty, score));
            }
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Take(Math.Clamp(take, 1, 20))
            .ToArray();
    }

    internal static bool IsNumericCode(string code)
    {
        if (code.Length is < 2 or > 16 || !char.IsDigit(code[0]) || !char.IsDigit(code[^1]))
        {
            return false;
        }

        foreach (var symbol in code)
        {
            if (!char.IsDigit(symbol) && symbol != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.ToLowerInvariant()
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .Select(token => token.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
