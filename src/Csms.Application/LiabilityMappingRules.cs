using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Csms.Application;

/// <summary>Детерминированные ключи и пороги сопоставления.</summary>
public static class LiabilityMappingRules
{
    /// <summary>Версия системного промпта арбитра — входит в ключ запуска и в БД.</summary>
    public const string PromptVersion = "liability-arbiter-v3";

    /// <summary>
    /// Версия правил retriever/валидатора. Отдельная от промпта: смена порогов
    /// или весов тоже должна создавать новый run_id, иначе старый результат
    /// будет считаться актуальным.
    /// </summary>
    public const string RulesVersion = "liability-rules-v3";

    /// <summary>
    /// Версия набора гипотез. Увеличивать при добавлении/правке ViolationKind
    /// или текста генератора: гипотезы уходят в промпт и в результат.
    /// </summary>
    public const string HypothesesVersion = "liability-hypotheses-v2";

    /// <summary>Ниже этой вероятности решение считается низкоуверенным.</summary>
    public const double LowProbabilityThreshold = 0.5;

    /// <summary>Частичное совпадение ниже этой вероятности уходит на ручную проверку.</summary>
    public const double PartialReviewThreshold = 0.7;

    /// <summary>Полное совпадение ниже этой вероятности тоже проверяется юристом.</summary>
    public const double FullReviewThreshold = 0.8;

    /// <summary>Максимум кандидатов в пуле (требование 25–30).</summary>
    public const int MaxCandidates = 30;

    /// <summary>Минимально желательный размер пула для высокого recall.</summary>
    public const int PreferredCandidates = 25;

    /// <summary>Ограничения источников: общие нормы не вытесняют специфичные.</summary>
    public const int VectorCandidateLimit = 24;
    public const int FullTextCandidateLimit = 18;
    public const int UmbrellaCandidateLimit = 6;
    /// <summary>Специальные нормы, найденные по доменной лексике обязанности.</summary>
    public const int DomainCandidateLimit = 20;

    /// <summary>Веса при слиянии оценок retriever'а.</summary>
    public const double VectorSearchWeight = 0.65;
    public const double FullTextSearchWeight = 0.35;
    public const double UmbrellaSearchWeight = 0.5;

    /// <summary>
    /// Повышение ранга специальной нормы, если в тексте встречается domain-лексика
    /// обязанности. Защищает от «общих» 1.1/2.9.1, которые не являются составом.
    /// </summary>
    public const double DomainSpecialBoost = 1.35;

    /// <summary>
    /// Тексты-маркеры норм, которые не являются ответственностью: освобождение,
    /// прекращение дела, отказ в возбуждении. Такие нормы не должны попадать в пул.
    /// </summary>
    public static readonly string[] NonLiabilityMarkers =
    [
        "освобождение от административной ответственности",
        "освобождению от административной ответственности",
        "прекращение дела об административном правонарушении",
        "прекращению дела об административном правонарушении",
        "отказ в возбуждении дела об административном правонарушении",
    ];

    /// <summary>Слова, характерные для обязанностей 158н в сфере 44-ФЗ.</summary>
    public static readonly string[] ProcurementDomainMarkers =
    [
        "план-график", "планов-графиков", "плана-графика", "планом-графиком",
        "планы-графиков", "план графика", "контрактная система",
        "контрактной системе", "заказчик", "закупк", "контракт", "извещение",
        "процедура закупки",
    ];

    /// <summary>
    /// Если обязанность говорит о план-графике, special-норма должна описывать
    /// именно план-график, а не любую закупку. Снимает проблему широкого FTS.
    /// </summary>
    public static readonly string[] PlanSchedulePhrases =
    [
        "план-график", "плана-графика", "планом-графиком",
        "планы-графиков", "план графика",
    ];

    /// <summary>
    /// Отпечаток всех правил, влияющих на вывод. Входит в run_id: правка любого
    /// порога/веса автоматически создаёт новую версию прогона.
    /// </summary>
    public static string BuildRulesFingerprint() =>
        string.Join('|',
            PromptVersion,
            RulesVersion,
            HypothesesVersion,
            Number(LowProbabilityThreshold),
            Number(PartialReviewThreshold),
            Number(FullReviewThreshold),
            MaxCandidates,
            PreferredCandidates,
            VectorCandidateLimit,
            FullTextCandidateLimit,
            UmbrellaCandidateLimit,
            DomainCandidateLimit,
            Number(VectorSearchWeight),
            Number(FullTextSearchWeight),
            Number(UmbrellaSearchWeight),
            Number(DomainSpecialBoost),
            string.Join(',', NonLiabilityMarkers),
            string.Join(',', ProcurementDomainMarkers),
            string.Join(',', PlanSchedulePhrases));

    private static string Number(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Ключ запуска: одинаковые входы дают одинаковый run_id.</summary>
    public static Guid BuildRunId(
        DutyInput duty,
        IReadOnlyList<LiabilityCandidate> candidates,
        string modelVersion,
        string configFingerprint = "")
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
            promptVersion = PromptVersion,
            rulesVersion = RulesVersion,
            hypothesesVersion = HypothesesVersion,
            rules = BuildRulesFingerprint(),
            configFingerprint
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
