using System.Text;
using System.Text.RegularExpressions;

namespace Csms.Infrastructure.Npa;

/// <summary>Итог разбора одного числа: код ОКПД2 и уверенность.</summary>
/// <param name="Code">Канонический код из справочника.</param>
/// <param name="Confidence">1.0 — точное совпадение, ниже — восстановление после OCR.</param>
/// <param name="Method">catalog / digits / structural — как получен код.</param>
public sealed record CodeResolution(string Code, double Confidence, string Method);

/// <summary>
/// Превращает распознанное на странице число в код ОКПД2. Главный фильтр —
/// сам справочник: код, которого в нём нет, отбрасывается. Поэтому номера
/// пунктов, сноски и суммы не превращаются в правила.
/// </summary>
public sealed partial class Okpd2CodeResolver(Okpd2Catalog catalog)
{
    [GeneratedRegex(@"^\d{2}(?:\.\d{1,3}){0,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex StructuralRegex();

    public Okpd2Catalog Catalog => catalog;

    public CodeResolution? Resolve(string? rawToken)
    {
        var token = Clean(rawToken);
        if (token is null)
        {
            return null;
        }

        if (catalog.Contains(token))
        {
            return new CodeResolution(token, 1.0, "catalog");
        }

        if (catalog.IsEmpty)
        {
            // Справочник ещё не загружен: работаем по структуре кода и честно
            // помечаем правило как требующее проверки.
            return StructuralRegex().IsMatch(token)
                ? new CodeResolution(token, 0.45, "structural")
                : null;
        }

        // OCR часто теряет точки: 262011110 → 26.20.11.110.
        var byDigits = catalog.CodeOfDigits(token.Replace(".", string.Empty, StringComparison.Ordinal));
        if (byDigits is not null)
        {
            return new CodeResolution(byDigits, 0.9, "digits");
        }

        return null;
    }

    /// <summary>
    /// Оставляет только цифры и точки. Всё остальное (буквы, скобки) означает,
    /// что распознан не код, а слово или сноска.
    /// </summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var symbol in raw)
        {
            if (char.IsDigit(symbol))
            {
                builder.Append(symbol);
            }
            else if (symbol is '.' or ',')
            {
                builder.Append('.');
            }
            else if (char.IsWhiteSpace(symbol))
            {
                continue;
            }
            else
            {
                return null;
            }
        }

        var text = builder.ToString().Trim('.');
        // Схлопываем двойные точки, которые появились из «26..20».
        while (text.Contains("..", StringComparison.Ordinal))
        {
            text = text.Replace("..", ".", StringComparison.Ordinal);
        }

        return text.Length is >= 2 and <= 16 ? text : null;
    }
}
