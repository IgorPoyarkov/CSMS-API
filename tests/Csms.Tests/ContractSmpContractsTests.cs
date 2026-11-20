using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Csms.Api;
using Csms.Domain;

namespace Csms.Tests;

/// <summary>HTTP-контракт: frontend-схема должна принимать ответ API без адаптера.</summary>
public class ContractSmpContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Request_ToDomain_ParsesStringsAndTrims()
    {
        var request = ContractSmpRecordSamples.Request() with
        {
            OrganizationId = " 1 ",
            Amount = "1 200 000.50",
            ContractPrice = "32000000.00",
            ReceivedAt = "2026-10-06T09:20",
            SupplierInn = " 3123001111 ",
        };

        var input = request.ToDomain();

        Assert.Equal("1", input.OrganizationId);
        Assert.Equal(1_200_000.50m, input.Amount);
        Assert.Equal(32_000_000m, input.ContractPrice);
        Assert.Equal("3123001111", input.SupplierInn);
        Assert.Equal(new DateOnly(2026, 10, 5), input.DocumentDate);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T09:20", CultureInfo.InvariantCulture), input.ReceivedAt);
    }

    [Fact]
    public void Request_FromJson_AcceptsNumericIds()
    {
        const string json = """
        {
          "organizationId": 1,
          "contractId": 12,
          "documentType": "subContract",
          "documentNumber": "5",
          "documentDate": "2026-10-05",
          "amount": "1200000",
          "priceType": "contract",
          "supplierName": "ООО «РосДорМонтаж»",
          "isSmpSubject": true,
          "receivedAt": "2026-10-06T09:20",
          "subContractRecordId": 7,
          "actor": "Подрядчик"
        }
        """;

        var request = JsonSerializer.Deserialize<ContractSmpRecordInputRequest>(json, JsonOptions);

        Assert.NotNull(request);
        Assert.Equal("1", request.OrganizationId);
        Assert.Equal("12", request.ContractId);
        Assert.Equal("7", request.SubContractRecordId);
        Assert.Equal(ContractSmpDocumentType.SubContract, request.DocumentType);
    }

    [Fact]
    public void Request_ToDomain_RejectsBadDate()
    {
        var request = ContractSmpRecordSamples.Request() with { DocumentDate = "05.10.2026" };

        var error = Assert.Throws<DomainException>(() => request.ToDomain());

        Assert.Contains("ГГГГ-ММ-ДД", error.Message);
    }

    [Fact]
    public void Response_UsesFrontendFieldNamesAndFormats()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);
        record.AddDocument(
            ContractSmpDocumentType.Declaration,
            "Декларация",
            "Декларация о принадлежности к СМП",
            ContractSmpRecordSamples.SupplierActor,
            ContractSmpRecordSamples.Now.AddMinutes(1));

        var response = ContractSmpRecordResponse.From(record);
        var json = JsonSerializer.Serialize(response, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("subContract", root.GetProperty("documentType").GetString());
        Assert.Equal("submitted", root.GetProperty("status").GetString());
        Assert.Equal("contract", root.GetProperty("priceType").GetString());
        Assert.Equal("1200000", root.GetProperty("amount").GetString());
        Assert.Equal("2026-10-05", root.GetProperty("documentDate").GetString());
        Assert.Equal("2026-10-06T09:20:00", root.GetProperty("receivedAt").GetString());
        Assert.Equal("3123001111", root.GetProperty("supplierInn").GetString());
        Assert.Equal("declaration", root.GetProperty("documents")[0].GetProperty("type").GetString());
        Assert.True(root.GetProperty("documents")[0].GetProperty("matchesHash").GetBoolean());

        Assert.Equal(record.Id, response.Id);
        Assert.Equal(2, response.Events.Count);
        Assert.Single(response.Documents);
        Assert.DoesNotContain(response.Issues, issue => issue.IsError);
    }
}
