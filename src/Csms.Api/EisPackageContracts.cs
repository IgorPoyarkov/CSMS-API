using Csms.Application;
using Csms.Domain;

namespace Csms.Api;

/// <summary>Тело запроса на создание пакета.</summary>
/// <remarks>
/// <c>PayloadXml</c> необязателен: для извещения и проекта контракта фронт
/// присылает готовый XML, для остальных типов шлюз собирает заглушку.
/// </remarks>
public sealed record CreatePackageRequest(
    EisDocumentType DocumentType,
    string? ContractEisNumber,
    string? Ikz,
    string? PayloadXml = null);

/// <summary>
/// Тело запроса на возврат отклонённого пакета в работу.
/// <remarks>
/// <c>PayloadXml</c> необязателен: без него сохраняется прежнее содержимое.
/// Это важно для извещений и проектов контракта, XML которых собирает фронт —
/// пересборка заглушкой потеряла бы готовый документ.
/// </remarks>
/// </summary>
public sealed record RevisePackageRequest(string? PayloadXml = null);

/// <summary>Подпись, полученная плагином на рабочем месте заказчика.</summary>
public sealed record AttachSignatureRequest(
    string? SignatureBase64,
    string? SignerThumbprint,
    string? SignedContentSha256);

/// <summary>Что подписывать: содержимое и его хеш.</summary>
public sealed record SignatureTaskResponse(
    string PayloadXml,
    string PayloadSha256,
    string SchemaVersion,
    bool IsPlaceholder)
{
    public static SignatureTaskResponse From(SignatureTask task) =>
        new(task.PayloadXml, task.PayloadSha256, task.SchemaVersion, task.IsPlaceholder);
}

/// <summary>
/// Пакет в том виде, в каком его видит фронт.
///
/// <c>isPlaceholderFormat</c> — честный флаг: содержимое собрано заглушкой, а
/// не по схеме ЕИС. Пока он <c>true</c>, пакет нельзя считать файлом обмена.
/// </summary>
public sealed record EisPackageResponse(
    Guid Id,
    EisDocumentType DocumentType,
    string ContractEisNumber,
    string Ikz,
    EisPackageStatus Status,
    string PayloadXml,
    string PayloadSha256,
    string PayloadSchemaVersion,
    bool IsPlaceholderFormat,
    string? SignatureBase64,
    string? SignerThumbprint,
    DateTimeOffset? SignedAt,
    string? EisGuid,
    string? LastError,
    IReadOnlyList<FlkError> FlkErrors,
    IReadOnlyList<EisPackageEvent> History,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static EisPackageResponse From(EisPackage package) =>
        new(
            package.Id,
            package.DocumentType,
            package.ContractEisNumber,
            package.Ikz,
            package.Status,
            package.PayloadXml,
            package.PayloadSha256,
            package.PayloadSchemaVersion,
            package.PayloadSchemaVersion.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase),
            package.SignatureBase64,
            package.SignerThumbprint,
            package.SignedAt,
            package.EisGuid,
            package.LastError,
            package.FlkErrors,
            package.History,
            package.CreatedAt,
            package.UpdatedAt);
}
