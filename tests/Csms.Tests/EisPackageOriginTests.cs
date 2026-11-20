using System.Text.Json;
using System.Text.Json.Serialization;
using Csms.Api;
using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Б4: пакет обязан помнить, от какого документа он пришёл, и снимок реквизитов
/// заказчика. Снимок фиксируется при создании и не меняется последующими
/// действиями над пакетом — иначе подпись нельзя будет объяснить.
/// </summary>
public sealed class EisPackageOriginTests
{
    private const string AcceptedContractNumber = "0373100000123000001";

    private static EisPackageService NewService() =>
        new(
            new InMemoryEisPackageRepository(),
            new MockEisTransport(),
            new PlaceholderXmlBuilder(),
            new EisSchemaCatalog(Path.Combine(Path.GetTempPath(), "csms-tests-no-schemas")),
            TimeProvider.System);

    [Fact]
    public async Task Create_StoresSourceAndCustomerSnapshot()
    {
        var service = NewService();
        var source = new EisPackageSource(
            EisSourceDocumentKind.Notice, "notice-1", "0148200000126000001");
        var customer = new EisCustomerSnapshot(
            FullName: "ГБУЗ «Городская поликлиника № 1»",
            Inn: "0148000000",
            Kpp: "014801001",
            RegNumber: "01482000001");

        var package = await service.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.SmpAttraction,
                AcceptedContractNumber,
                "2612345678901234567890",
                Source: source,
                Customer: customer),
            CancellationToken.None);

        Assert.Equal(source, package.Source);
        Assert.Equal(customer, package.Customer);

        var response = EisPackageResponse.From(package);
        Assert.Equal(source, response.Source);
        Assert.Equal(customer, response.Customer);
    }

    [Fact]
    public async Task Create_WithoutOrigin_KeepsBackwardCompatibility()
    {
        var service = NewService();

        var package = await service.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.SmpAttraction,
                AcceptedContractNumber,
                "2612345678901234567890"),
            CancellationToken.None);

        Assert.Null(package.Source);
        Assert.Null(package.Customer);
    }

    [Fact]
    public async Task Create_TrimsSourceAndCustomerFields()
    {
        var service = NewService();

        var package = await service.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.SmpAttraction,
                AcceptedContractNumber,
                null,
                Source: new EisPackageSource(EisSourceDocumentKind.ContractProject, "  cp-7  ", "  42  "),
                Customer: new EisCustomerSnapshot(Inn: "  7700000000  ", Okfs: "  ")),
            CancellationToken.None);

        Assert.Equal("cp-7", package.Source!.DocumentId);
        Assert.Equal("42", package.Source.DocumentNumber);
        Assert.Equal("7700000000", package.Customer!.Inn);
        Assert.Null(package.Customer.Okfs);
    }

    [Fact]
    public async Task Create_RequiresDocumentIdInSource()
    {
        var service = NewService();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync(
                new CreatePackageCommand(
                    EisDocumentType.SmpAttraction,
                    AcceptedContractNumber,
                    null,
                    Source: new EisPackageSource(EisSourceDocumentKind.Notice, "   ")),
                CancellationToken.None));

        Assert.Contains("source.documentId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revise_DoesNotOverwriteOrigin()
    {
        const string rejectedNumber = "0373100000123000000";
        var service = NewService();
        var source = new EisPackageSource(EisSourceDocumentKind.Notice, "notice-2");
        var customer = new EisCustomerSnapshot(Inn: "0148000000");

        var package = await service.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.SmpAttraction,
                rejectedNumber,
                null,
                Source: source,
                Customer: customer),
            CancellationToken.None);

        var task = await service.PrepareSignatureAsync(package.Id, CancellationToken.None);
        await service.AttachSignatureAsync(
            package.Id,
            new AttachSignatureCommand("MIIC", "thumbprint", task.PayloadSha256),
            CancellationToken.None);
        await service.SendAsync(package.Id, CancellationToken.None);
        await service.RefreshStatusAsync(package.Id, CancellationToken.None);
        package = await service.ReviseAsync(package.Id, CancellationToken.None);

        Assert.Equal(source, package.Source);
        Assert.Equal(customer, package.Customer);
    }

    [Fact]
    public void Request_DeserializesFieldsSentByFrontend()
    {
        // Те же настройки, что в Program.cs: camelCase и строки вместо чисел
        // для перечислений. Если они не совпадут, фронт отправит source, а
        // шлюз его молча потеряет.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        const string json = """
            {
              "documentType": "Notification",
              "contractEisNumber": "0373100000123000001",
              "ikz": "2612345678901234567890",
              "source": {
                "documentKind": "Notice",
                "documentId": "1",
                "documentNumber": "0148200000126000001"
              },
              "customer": {
                "inn": "0148000000",
                "regNumber": "01482000001"
              }
            }
            """;

        var request = JsonSerializer.Deserialize<CreatePackageRequest>(json, options);

        Assert.NotNull(request);
        Assert.Equal(EisDocumentType.Notification, request!.DocumentType);
        Assert.Equal(EisSourceDocumentKind.Notice, request.Source!.DocumentKind);
        Assert.Equal("1", request.Source.DocumentId);
        Assert.Equal("0148000000", request.Customer!.Inn);
        Assert.Equal("01482000001", request.Customer.RegNumber);
    }

    [Fact]
    public void Restore_KeepsOriginFromStorage()
    {
        var source = new EisPackageSource(EisSourceDocumentKind.Contract, "contract-3");
        var customer = new EisCustomerSnapshot(FullName: "Заказчик");

        var package = EisPackage.Restore(
            Guid.NewGuid(),
            EisDocumentType.ContractExecution,
            AcceptedContractNumber,
            "2612345678901234567890",
            "<packet/>",
            "17.0",
            null,
            null,
            null,
            null,
            EisPackageStatus.Draft,
            null,
            [],
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            source,
            customer);

        Assert.Equal(source, package.Source);
        Assert.Equal(customer, package.Customer);
    }
}
