using Csms.Domain;

namespace Csms.Application;

/// <summary>Состояние пакета по мнению ЕИС.</summary>
public enum EisStatusKind
{
    /// <summary>ЕИС ещё проверяет, результата нет.</summary>
    InProgress = 0,

    /// <summary>Пакет принят.</summary>
    Accepted = 1,

    /// <summary>Пакет отклонён, есть ошибки ФЛК.</summary>
    Rejected = 2,
}

/// <summary>Ответ шлюза на отправку: идентификатор пакета в ЕИС.</summary>
public sealed record EisSendResult(string EisGuid);

/// <summary>Ответ шлюза на запрос результата проверки.</summary>
public sealed record EisStatusResult(
    EisStatusKind Kind,
    string? EisGuid,
    IReadOnlyList<FlkError> Errors);

/// <summary>
/// Транспорт до ЕИС.
///
/// За интерфейсом сейчас заглушка, потом — SOAP поверх ГОСТ-mTLS. Прикладной
/// слой от этого не меняется: он не знает ни про SOAP, ни про сертификаты,
/// только про «отправить» и «спросить результат».
/// </summary>
public interface IEisTransport
{
    Task<EisSendResult> SendAsync(EisPackage package, CancellationToken cancellationToken);

    Task<EisStatusResult> GetStatusAsync(EisPackage package, CancellationToken cancellationToken);
}

/// <summary>Транспорт не смог доставить пакет: сеть, шлюз, сертификат канала.</summary>
public sealed class EisTransportException : Exception
{
    public EisTransportException(string message)
        : base(message)
    {
    }
}
