using Csms.Domain;

namespace Csms.Application;

/// <summary>Запрос на создание пакета.</summary>
public sealed record CreatePackageCommand(
    EisDocumentType DocumentType,
    string? ContractEisNumber,
    string? Ikz);

/// <summary>Подпись, полученная на рабочем месте заказчика.</summary>
public sealed record AttachSignatureCommand(
    string? SignatureBase64,
    string? SignerThumbprint,
    string? SignedContentSha256);

/// <summary>
/// Что именно подписывать.
///
/// Фронт получает содержимое и его SHA-256, подписывает байты плагином
/// (ключ остаётся в токене) и возвращает подпись вместе с хешем. Сервер
/// сверяет хеш со своим — так подпись под другим документом не пройдёт.
/// </summary>
public sealed record SignatureTask(
    string PayloadXml,
    string PayloadSha256,
    string SchemaVersion,
    bool IsPlaceholder);
