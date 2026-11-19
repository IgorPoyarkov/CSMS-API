using System.Collections.Concurrent;
using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Заглушка шлюза ЕИС: без сети, сертификатов и ГОСТ-крипты, но с тем же
/// контрактом, что будет у настоящего транспорта.
///
/// Сценарии детерминированы и привязаны к номеру контракта, чтобы проверки
/// были воспроизводимы, а не «иногда падает»:
///
///   * номер оканчивается на <c>0000</c> — ФЛК отклоняет пакет (две ошибки);
///   * номер оканчивается на <c>9999</c> — шлюз недоступен (транспортная ошибка);
///   * остальные — первые два запроса статуса «в проверке», третий «принято».
/// </summary>
public sealed class MockEisTransport : IEisTransport
{
    private const int PollsBeforeResult = 3;

    private readonly ConcurrentDictionary<string, int> _polls = new(StringComparer.Ordinal);

    public Task<EisSendResult> SendAsync(EisPackage package, CancellationToken cancellationToken)
    {
        if (package.ContractEisNumber.EndsWith("9999", StringComparison.Ordinal))
        {
            throw new EisTransportException(
                "Шлюз ЕИС недоступен (имитация): истёк таймаут соединения. Отправку можно повторить.");
        }

        // Даже заглушка должна собирать архив: ошибки упаковки (нет подписи,
        // битый base64) тогда видны сразу, а не после подключения реального
        // шлюза. Base64 здесь не передаётся — проверяется только собираемость.
        _ = EisPackageArchiver.BuildBase64Zip(package);

        var eisGuid = Guid.NewGuid().ToString("D");
        _polls[eisGuid] = 0;

        return Task.FromResult(new EisSendResult(eisGuid));
    }

    public Task<EisStatusResult> GetStatusAsync(EisPackage package, CancellationToken cancellationToken)
    {
        var eisGuid = package.EisGuid
            ?? throw new EisTransportException("В пакете нет eisGuid: запрашивать результат не по чему.");

        var polls = _polls.AddOrUpdate(eisGuid, 1, (_, current) => current + 1);

        if (package.ContractEisNumber.EndsWith("0000", StringComparison.Ordinal))
        {
            FlkError[] errors =
            [
                new FlkError(
                    "FLK-101",
                    "Отсутствует обязательное поле «сведения о привлечении СМП».",
                    Field: "smpAttraction",
                    Hint: "Заполните объём привлечения и перечень субподрядчиков."),
                new FlkError(
                    "FLK-205",
                    "Сумма привлечения превышает цену контракта.",
                    Field: "smpAttraction.volume",
                    Hint: "Проверьте сумму: она не может быть больше цены контракта."),
            ];

            return Task.FromResult(new EisStatusResult(EisStatusKind.Rejected, eisGuid, errors));
        }

        var kind = polls < PollsBeforeResult ? EisStatusKind.InProgress : EisStatusKind.Accepted;

        return Task.FromResult(new EisStatusResult(kind, eisGuid, []));
    }
}
