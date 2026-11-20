using Csms.Domain;

namespace Csms.Application;

/// <summary>Запрос на создание пакета.</summary>
/// <remarks>
/// <c>PayloadXml</c> — готовый XML документа CSMS (извещение или проект
/// контракта). Если он передан, шлюз не собирает заглушку, а подписывает и
/// отправляет этот документ. Для сведений СМП и исполнения контракта
/// остаётся <c>null</c> — их собирает <c>IEisDocumentXmlBuilder</c>.
/// </remarks>
public sealed record CreatePackageCommand(
    EisDocumentType DocumentType,
    string? ContractEisNumber,
    string? Ikz,
    string? PayloadXml = null);

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
