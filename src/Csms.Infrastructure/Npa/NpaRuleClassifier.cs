using System.Text.RegularExpressions;
using Csms.Application;

namespace Csms.Infrastructure.Npa;

/// <summary>Решение по одной строке акта.</summary>
public sealed record RuleDecision(
    string RuleType,
    string? ConditionText,
    double Confidence,
    string Status,
    string? Note);

/// <summary>
/// Классификация строки акта: входит код в перечень, выведен из него («кроме»)
/// или действует при условии. Работает детерминированно по формулировкам
/// 44-ФЗ; спорные строки не угадываются, а уходят в очередь на проверку.
/// </summary>
public static partial class NpaRuleClassifier
{
    private static readonly string[] ExclusionMarkers =
    [
        "кроме", "за исключением", "исключ", "не применя", "не распространя",
    ];

    private static readonly string[] ConditionalMarkers =
    [
        "при условии", "в случае если", "только при", "если закупка", "если цена",
        "при наличии", "при осуществлении закупки",
    ];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public static RuleDecision Decide(
        string? name,
        string? quote,
        double codeConfidence,
        double minAutoConfidence)
    {
        var text = Compact($"{name} {quote}");

        // «Кроме мебели» внутри наименования позиции — это уточнение объёма
        // включения, а не исключение всей строки. Исключением считается только
        // отдельная оговорка, где нет кода таблицы (обработана в билдере).
        var exclusion = string.IsNullOrWhiteSpace(name)
            ? ExclusionMarkers.FirstOrDefault(marker =>
                text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            : null;

        if (exclusion is not null)
        {
            return new RuleDecision(
                NpaRuleTypes.Exclude,
                Clip(text),
                Math.Min(codeConfidence, 0.9),
                NpaRuleStatuses.Auto,
                $"исключение по формулировке «{exclusion}»");
        }

        var conditional = ConditionalMarkers.FirstOrDefault(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (conditional is not null)
        {
            return new RuleDecision(
                NpaRuleTypes.Conditional,
                Clip(text),
                Math.Min(codeConfidence, 0.75),
                NpaRuleStatuses.NeedsReview,
                $"условие применения: «{conditional}»");
        }

        var confidence = codeConfidence;
        string? note = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            confidence = Math.Min(confidence, 0.6);
            note = "наименование позиции не распознано";
        }

        var status = confidence >= minAutoConfidence
            ? NpaRuleStatuses.Auto
            : NpaRuleStatuses.NeedsReview;

        return new RuleDecision(NpaRuleTypes.Include, null, confidence, status, note);
    }

    private static string Compact(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Clip(string text) => text.Length <= 400 ? text : text[..400];
}
