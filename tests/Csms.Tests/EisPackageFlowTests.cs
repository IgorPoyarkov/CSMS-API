using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Сквозной сценарий через настоящие слои (без HTTP): создание → подпись →
/// отправка → ФЛК. Транспорт — заглушка, поэтому проверка воспроизводима:
/// исход зависит только от номера контракта.
/// </summary>
public sealed class EisPackageFlowTests
{
    /// <summary>Обычный номер: ЕИС в итоге принимает пакет.</summary>
    private const string AcceptedContractNumber = "0373100000123000001";

    /// <summary>Оканчивается на 0000: заглушка возвращает ошибки ФЛК.</summary>
    private const string RejectedContractNumber = "0373100000123000000";

    /// <summary>Оканчивается на 9999: заглушка имитирует недоступность шлюза.</summary>
    private const string UnreachableContractNumber = "0373100000123009999";

    private const string Signature = "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT";

    private static EisPackageService NewService() =>
        new(
            new InMemoryEisPackageRepository(),
            new MockEisTransport(),
            new PlaceholderXmlBuilder(),
            new EisSchemaCatalog(Path.Combine(Path.GetTempPath(), "csms-tests-no-schemas")),
            TimeProvider.System);

    private static async Task<EisPackage> CreateAsync(EisPackageService service, string number)
    {
        var command = new CreatePackageCommand(EisDocumentType.SmpAttraction, number, "2612345678901234567890");

        return await service.CreateAsync(command, CancellationToken.None);
    }

    private static async Task<EisPackage> SignAsync(EisPackageService service, Guid id)
    {
        var task = await service.PrepareSignatureAsync(id, CancellationToken.None);

        return await service.AttachSignatureAsync(
            id,
            new AttachSignatureCommand(Signature, "thumbprint", task.PayloadSha256),
            CancellationToken.None);
    }

    [Fact]
    public async Task Сценарий_приёма_проходит_целиком()
    {
        var service = NewService();
        var package = await CreateAsync(service, AcceptedContractNumber);

        Assert.Equal(EisPackageStatus.Draft, package.Status);
        Assert.True(package.PayloadSchemaVersion.StartsWith("placeholder", StringComparison.Ordinal));

        package = await SignAsync(service, package.Id);
        Assert.Equal(EisPackageStatus.Signed, package.Status);

        package = await service.SendAsync(package.Id, CancellationToken.None);
        Assert.Equal(EisPackageStatus.SentToEis, package.Status);
        Assert.False(string.IsNullOrWhiteSpace(package.EisGuid));

        package = await service.RefreshStatusAsync(package.Id, CancellationToken.None);
        Assert.Equal(EisPackageStatus.FlkChecking, package.Status);

        package = await service.RefreshStatusAsync(package.Id, CancellationToken.None);
        Assert.Equal(EisPackageStatus.FlkChecking, package.Status);

        package = await service.RefreshStatusAsync(package.Id, CancellationToken.None);
        Assert.Equal(EisPackageStatus.AcceptedInLk, package.Status);
        Assert.Empty(package.FlkErrors);
    }

    [Fact]
    public async Task Отказ_ФЛК_приносит_ошибки_с_полями()
    {
        var service = NewService();
        var package = await CreateAsync(service, RejectedContractNumber);
        package = await SignAsync(service, package.Id);
        package = await service.SendAsync(package.Id, CancellationToken.None);

        package = await service.RefreshStatusAsync(package.Id, CancellationToken.None);

        Assert.Equal(EisPackageStatus.RejectedByFlk, package.Status);
        Assert.Equal(2, package.FlkErrors.Count);
        Assert.All(package.FlkErrors, error => Assert.False(string.IsNullOrWhiteSpace(error.Field)));
    }

    [Fact]
    public async Task Отклонённый_пакет_можно_вернуть_в_работу_и_подписать_заново()
    {
        var service = NewService();
        var package = await CreateAsync(service, RejectedContractNumber);
        package = await SignAsync(service, package.Id);
        package = await service.SendAsync(package.Id, CancellationToken.None);
        package = await service.RefreshStatusAsync(package.Id, CancellationToken.None);

        package = await service.ReviseAsync(package.Id, CancellationToken.None);

        Assert.Equal(EisPackageStatus.Draft, package.Status);
        Assert.Empty(package.FlkErrors);
        Assert.Null(package.SignatureBase64);

        package = await SignAsync(service, package.Id);
        Assert.Equal(EisPackageStatus.Signed, package.Status);
    }

    [Fact]
    public async Task Недоступность_шлюза_становится_состоянием_а_не_ошибкой_запроса()
    {
        var service = NewService();
        var package = await CreateAsync(service, UnreachableContractNumber);
        package = await SignAsync(service, package.Id);

        package = await service.SendAsync(package.Id, CancellationToken.None);

        Assert.Equal(EisPackageStatus.SendFailed, package.Status);
        Assert.False(string.IsNullOrWhiteSpace(package.LastError));
    }

    [Fact]
    public async Task Повторная_отправка_после_транспортной_ошибки_разрешена()
    {
        var service = NewService();
        var package = await CreateAsync(service, UnreachableContractNumber);
        package = await SignAsync(service, package.Id);
        package = await service.SendAsync(package.Id, CancellationToken.None);

        // шлюз всё ещё недоступен — проверяем именно то, что повтор не запрещён правилами
        package = await service.SendAsync(package.Id, CancellationToken.None);

        Assert.Equal(EisPackageStatus.SendFailed, package.Status);
    }

    [Fact]
    public async Task Подписать_дважды_нельзя()
    {
        var service = NewService();
        var package = await CreateAsync(service, AcceptedContractNumber);
        package = await SignAsync(service, package.Id);

        await Assert.ThrowsAsync<DomainException>(() =>
            service.PrepareSignatureAsync(package.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Список_отдаёт_пакеты_свежие_сверху()
    {
        var service = NewService();
        await CreateAsync(service, AcceptedContractNumber);
        await Task.Delay(10);
        var latest = await CreateAsync(service, AcceptedContractNumber);

        var packages = await service.ListAsync(CancellationToken.None);

        Assert.Equal(2, packages.Count);
        Assert.Equal(latest.Id, packages[0].Id);
    }
}
