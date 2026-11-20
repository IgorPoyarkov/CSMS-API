namespace Csms.Application;

/// <summary>
/// Справочник правил НПА по кодам ОКПД2: аукционный перечень (РП № 471-р),
/// национальный режим (ПП № 1875), оценка заявок (ПП № 2604) и любые другие
/// акты, где перечислены коды ОКПД2.
///
/// Правила не вводятся руками: агент разбирает официальный текст акта с
/// publication.pravo.gov.ru, извлекает коды и условия и публикует новую версию
/// правил. Версии не перезаписываются, активная — одна.
/// </summary>
public static class NpaRuleTypes
{
    /// <summary>Код входит в перечень: ограничение действует.</summary>
    public const string Include = "include";

    /// <summary>Код выведен из перечня («кроме», «за исключением»).</summary>
    public const string Exclude = "exclude";

    /// <summary>Правило действует только при условии из цитаты.</summary>
    public const string Conditional = "conditional";
}

/// <summary>Состояние правила: применено автоматически или ждёт человека.</summary>
public static class NpaRuleStatuses
{
    /// <summary>Извлечено детерминированно и подтверждено справочником ОКПД2.</summary>
    public const string Auto = "auto";

    /// <summary>Неоднозначно: код не найден в справочнике или условие спорное.</summary>
    public const string NeedsReview = "needs_review";

    public const string Approved = "approved";

    public const string Rejected = "rejected";
}

/// <summary>Итог последней проверки официальной публикации акта.</summary>
public static class NpaCheckStatuses
{
    public const string UpToDate = "up_to_date";

    public const string Published = "published";

    public const string Prepared = "prepared";

    public const string Error = "error";
}

/// <summary>Версия правил по одному акту (одна выгрузка официального текста).</summary>
public sealed record NpaDocumentInfo(
    string Code,
    string Version,
    string? EoNumber,
    string? Number,
    string Title,
    DateOnly? DocumentDate,
    DateOnly? PublishDate,
    int PagesCount,
    string? SourceUrl,
    string? PdfSha256,
    string? OcrEngine,
    DateTimeOffset FetchedAt,
    DateTimeOffset? LastCheckedAt,
    string? LastCheckStatus,
    string? LastCheckMessage,
    int ItemCount,
    int UnresolvedCount,
    string? UnresolvedJson,
    string Hash,
    bool IsActive);

/// <summary>
/// Одно правило по коду ОКПД2. Code — акт, которому принадлежит правило;
/// Id — конкретная запись: по одному коду их может быть несколько (например,
/// прямое правило первого агента и предложение LLM с другой цитатой).
/// </summary>
public sealed record NpaRuleInfo(
    Guid Id,
    string Code,
    string Okpd2Code,
    string RuleType,
    string? ConditionText,
    string? Quote,
    bool IsInherited,
    string? InheritedFrom,
    double Confidence,
    string Status,
    string? Note,
    int? SourcePage,
    int? SourceRow);

/// <summary>Правило в составе импорта: то же, что NpaRuleInfo, без кода акта.</summary>
public sealed record NpaRuleImportItem(
    string Okpd2Code,
    string RuleType,
    string? ConditionText,
    string? Quote,
    bool IsInherited,
    string? InheritedFrom,
    double Confidence,
    string Status,
    string? Note,
    int? SourcePage,
    int? SourceRow);

/// <summary>Готовая версия правил, которую агент публикует одной транзакцией.</summary>
public sealed record NpaDocumentImport(
    string Code,
    string Version,
    string? EoNumber,
    string? Number,
    string Title,
    DateOnly? DocumentDate,
    DateOnly? PublishDate,
    int PagesCount,
    string? SourceUrl,
    string? PdfSha256,
    string? OcrEngine,
    IReadOnlyList<NpaRuleImportItem> Rules,
    string? UnresolvedJson = null,
    bool Promote = true);

/// <summary>Событие смены активной версии правил.</summary>
public sealed record NpaDocumentUpdateEvent(
    string Code,
    string FromVersion,
    string ToVersion,
    int RuleCount,
    string Hash,
    DateTimeOffset FetchedAt);

/// <summary>
/// Действующее правило для конкретного кода ОКПД2: самое длинное совпадение
/// префикса, прямое правило важнее унаследованного.
/// </summary>
public sealed record NpaEffectiveRule(
    string Code,
    string Okpd2Code,
    string MatchedCode,
    string RuleType,
    string? ConditionText,
    bool IsInherited,
    string? InheritedFrom,
    double Confidence,
    string Status,
    string Version);

/// <summary>Кандидат ОКПД2, который передаётся модели вместе с текстом акта.</summary>
public sealed record NpaLlmCandidate(string Code, string Name);

/// <summary>Спорная строка акта и допустимые для неё коды из справочника.</summary>
public sealed record NpaLlmInputRow(
    string Text,
    int? Page,
    int? RowIndex,
    IReadOnlyList<NpaLlmCandidate> Candidates);

/// <summary>Запрос к LLM-аналитику по одному акту.</summary>
public sealed record NpaLlmAnalysisRequest(
    string Code,
    string Title,
    string? Number,
    IReadOnlyList<NpaLlmInputRow> Rows);

/// <summary>
/// Вывод LLM-аналитика. Код берётся только из переданных кандидатов, цитата —
/// из текста акта; нарушения отбрасываются до попадания в правила.
/// </summary>
public sealed record NpaLlmSuggestion(
    string Okpd2Code,
    string ItemName,
    string MatchType,
    string RuleType,
    IReadOnlyList<string> Conditions,
    string SourceQuote,
    bool RequiresHumanReview,
    int? Page,
    int? RowIndex);

/// <summary>Итог разбора спорных строк моделью (без публикации версии).</summary>
public sealed record NpaLlmAnalysisResult(
    string Code,
    string Model,
    int InputRows,
    int SuggestionCount,
    int RejectedCount,
    IReadOnlyList<NpaLlmSuggestion> Suggestions,
    string? Error);

/// <summary>Хранилище версий правил НПА.</summary>
public interface INpaRuleStore
{
    Task<IReadOnlyList<NpaDocumentInfo>> GetDocumentsAsync(CancellationToken cancellationToken = default);

    Task<NpaDocumentInfo?> GetActiveAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NpaRuleInfo>> GetRulesAsync(
        string code,
        string? search,
        string? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        bool directOnly = false);

    /// <summary>
    /// Действующее правило для кода ОКПД2: ближайший предок (включая сам код).
    /// Ищется по списку предков, а не перебором всех правил.
    /// </summary>
    Task<NpaEffectiveRule?> ResolveAsync(
        string code,
        string okpd2Code,
        bool includePending,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Принудительная переразметка создаёт новую версию, не перезаписывая
    /// историю. Проверка нужна, чтобы подобрать свободный суффикс (-r2, -r3).
    /// </summary>
    Task<bool> VersionExistsAsync(
        string code,
        string version,
        CancellationToken cancellationToken = default);

    Task<NpaDocumentUpdateEvent> PublishAsync(
        NpaDocumentImport import,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отметить проверку официального текста, даже если версия правил не
    /// изменилась. Это отдельное время от FetchedAt (момента публикации версии).
    /// </summary>
    Task<NpaDocumentInfo?> MarkCheckedAsync(
        string code,
        string status,
        string? message,
        DateTimeOffset? checkedAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Подтвердить или отклонить конкретную запись правила. Кодованный
    /// поиск здесь не годится: один код может иметь несколько правил с
    /// разными цитатами, и человек должен выбирать именно одно из них.
    /// </summary>
    Task<NpaRuleInfo?> VerifyAsync(
        string code,
        Guid ruleId,
        string status,
        string? note,
        CancellationToken cancellationToken = default);
}

/// <summary>Утилиты версионирования и нормализации кодов ОКПД2.</summary>
public static class NpaRuleSets
{
    /// <summary>Хеш набора правил: меняется при любом изменении состава.</summary>
    public static string ComputeHash(NpaDocumentImport import)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(import.Code).Append('|').Append(import.Version).Append('|');

        foreach (var rule in import.Rules
                     .OrderBy(rule => rule.Okpd2Code, StringComparer.Ordinal)
                     .ThenBy(rule => rule.RuleType, StringComparer.Ordinal))
        {
            builder.Append(rule.Okpd2Code).Append(':')
                .Append(rule.RuleType).Append(':')
                .Append(rule.IsInherited ? '1' : '0').Append(':')
                .Append(rule.ConditionText).Append(';');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Код ОКПД2 в каноническом виде: точки-разделители, без пробелов.</summary>
    public static string NormalizeCode(string code) =>
        code.Trim().Replace(',', '.').Replace(" ", string.Empty).Trim('.');

    /// <summary>Родительский код ОКПД2 (26.20.11.110 → 26.20.11).</summary>
    public static string? ParentOf(string code)
    {
        var index = code.LastIndexOf('.');
        return index <= 0 ? null : code[..index];
    }

    /// <summary>
    /// Проверяет, что код-кандидат входит в поддерево родителя. Сравнение по
    /// границе точки: 01.4 не является родителем 01.41.
    /// </summary>
    public static bool IsUnder(string code, string parent) =>
        code.Length > parent.Length &&
        code.StartsWith(parent, StringComparison.Ordinal) &&
        code[parent.Length] == '.';
}
