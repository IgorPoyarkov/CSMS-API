using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Csms.Application;

/// <summary>Детерминированные ключи и пороги сопоставления.</summary>
public static class LiabilityMappingRules
{
    /// <summary>Версия системного промпта арбитра — входит в ключ запуска и в БД.</summary>
    public const string PromptVersion = "liability-arbiter-v1";

    /// <summary>Ниже этой вероятности решение считается низкоуверенным.</summary>
    public const double LowProbabilityThreshold = 0.65;

    /// <summary>Частичное совпадение ниже этой вероятности уходит на ручную проверку.</summary>
    public const double PartialReviewThreshold = 0.75;

    /// <summary>Максимум кандидатов в пуле (требование 25–30).</summary>
    public const int MaxCandidates = 30;

    /// <summary>Минимально желательный размер пула для высокого recall.</summary>
    public const int PreferredCandidates = 25;

    /// <summary>Ключ запуска: одинаковые входы дают одинаковый run_id.</summary>
    public static Guid BuildRunId(
        DutyInput duty,
        IReadOnlyList<LiabilityCandidate> candidates,
        string modelVersion)
    {
        var nodeIds = candidates
            .Select(candidate => candidate.LiabilityNodeId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var canonical = new
        {
            dutyId = duty.Identifiers.DutyId,
            statement = duty.Statement,
            @object = duty.Object,
            subject = duty.Subject,
            deadline = duty.Deadline,
            condition = duty.Condition,
            actCode = duty.LiabilityActCode,
            nodeIds,
            modelVersion,
            promptVersion = PromptVersion
        };

        var json = JsonSerializer.Serialize(canonical);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Разбиение кандидатов на батчи с ограничением контекста.</summary>
public static class LiabilityBatching
{
    public const int DefaultMaxItems = 8;
    public const int DefaultMaxChars = 24_000;
    public const int MaxCandidateTextChars = 1_200;

    /// <summary>
    /// Батчи сохраняют порядок кандидатов; слишком длинный кандидат не теряется,
    /// а уходит отдельным батчем (текст будет усечён при сборке промпта).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<LiabilityCandidate>> Split(
        IReadOnlyList<LiabilityCandidate> candidates,
        int maxItems = DefaultMaxItems,
        int maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maxItems <= 0 || maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Размер батча должен быть положительным.");
        }

        var batches = new List<IReadOnlyList<LiabilityCandidate>>();
        var current = new List<LiabilityCandidate>();
        var currentChars = 0;

        foreach (var candidate in candidates)
        {
            var size = Math.Min(candidate.Text.Length, MaxCandidateTextChars)
                + candidate.NormReference.Length
                + 256; // реквизиты и служебные поля

            if (current.Count > 0 && (current.Count >= maxItems || currentChars + size > maxChars))
            {
                batches.Add(current.ToArray());
                current = [];
                currentChars = 0;
            }

            current.Add(candidate);
            currentChars += size;
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }
}
