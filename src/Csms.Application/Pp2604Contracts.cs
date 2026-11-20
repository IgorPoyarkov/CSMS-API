using System.Text.RegularExpressions;

namespace Csms.Application;

/// <summary>Откуда взято приложение N 2 и по какому справочнику выведены связи.</summary>
public sealed record Pp2604SourceInfo(
    string Act,
    string Appendix,
    string Portal,
    string EoNumber,
    string PdfPages,
    string Classifier);

/// <summary>Предельные величины одной позиции приложения N 2.</summary>
public sealed record Pp2604CategoryLimit(
    string Id,
    string Label,
    int AppendixRow,
    string AppendixName,
    int MinPrice,
    int? MaxExpenses,
    int MaxNonCost,
    int SourcePage);

/// <summary>
/// Выводимая связь «ОКПД2 → позиция приложения». Сам акт кодов не содержит,
/// поэтому источник и признак ручной проверки обязательны.
/// </summary>
public sealed record Pp2604Okpd2Link(
    string Prefix,
    string Category,
    int Row,
    string Basis,
    string ClassifierName,
    string? Condition,
    bool NeedsReview,
    string? Purpose,
    string Source,
    int SortOrder);

/// <summary>Активная версия каталога ПП РФ N 2604, прочитанная из БД.</summary>
public sealed record Pp2604Mapping(
    string Version,
    string RulesVersion,
    string EoNumber,
    string ApiUrl,
    string PdfUrl,
    string PdfSha256,
    string DocumentTitle,
    string? DocumentDate,
    string? PublishDate,
    int? PagesCount,
    string AppendixPages,
    string ExtractionMethod,
    DateTimeOffset ImportedAt,
    Pp2604SourceInfo Source,
    IReadOnlyList<Pp2604CategoryLimit> Categories,
    IReadOnlyList<Pp2604Okpd2Link> Rules);

/// <summary>Хранилище импортированного каталога 2604.</summary>
public interface IPp2604Catalog
{
    Task<Pp2604Mapping?> GetActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Результат подбора позиции приложения N 2 по коду ОКПД2.</summary>
public sealed record Pp2604Resolution(
    string Code,
    string? Category,
    int? Row,
    string? Basis,
    string? Source,
    bool NeedsReview,
    string? Reason);

/// <summary>
/// Приоритет: цель закупки → самый длинный префикс → остаток позиции 2.
/// Логика совпадает с фронтенд-проверками, но читает правила из БД.
/// </summary>
public static class Pp2604CategoryResolver
{
    private static readonly Regex CodePattern = new(
        @"^\d{2}(?:\.\d{1,3}){0,3}$", RegexOptions.Compiled);

    public static Pp2604Resolution Resolve(
        Pp2604Mapping mapping, string? code, string? purpose = null)
    {
        var normalized = Normalize(code);
        if (normalized.Length == 0)
        {
            return new Pp2604Resolution(normalized, null, null, null, null, true,
                "Код ОКПД2 не указан");
        }

        if (!CodePattern.IsMatch(normalized))
        {
            return new Pp2604Resolution(normalized, null, null, null, null, true,
                $"Код ОКПД2 «{normalized}» имеет некорректный формат");
        }

        var digits = normalized.Replace(".", string.Empty, StringComparison.Ordinal);

        if (purpose == "food_for_social_orgs" &&
            (digits.StartsWith("10", StringComparison.Ordinal) ||
             digits.StartsWith("11", StringComparison.Ordinal)))
        {
            var byPurpose = mapping.Rules.Where(rule => rule.Purpose == "food_for_social_orgs")
                .OrderBy(rule => rule.SortOrder)
                .FirstOrDefault();
            if (byPurpose is not null)
            {
                return ToResolution(normalized, byPurpose, false);
            }
        }

        var match = mapping.Rules
            .Where(rule => rule.Purpose is null &&
                           digits.StartsWith(
                               rule.Prefix.Replace(".", string.Empty, StringComparison.Ordinal),
                               StringComparison.Ordinal))
            .OrderByDescending(rule => rule.Prefix.Length)
            .ThenBy(rule => rule.SortOrder)
            .FirstOrDefault();

        return match is null
            ? new Pp2604Resolution(normalized, null, null, null, null, true,
                "Не удалось подобрать правило ОКПД2")
            : ToResolution(normalized, match, match.NeedsReview);
    }

    private static Pp2604Resolution ToResolution(
        string code, Pp2604Okpd2Link rule, bool needsReview) =>
        new(code, rule.Category, rule.Row, rule.Basis, rule.Source, needsReview, rule.Condition);

    private static string Normalize(string? code) =>
        (code ?? string.Empty).Trim().Replace(',', '.')
            .Replace(" ", string.Empty, StringComparison.Ordinal);
}
