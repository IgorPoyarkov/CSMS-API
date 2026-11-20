using Csms.Api;
using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Контракты HTTP-слоя: DTO не должны «додумывать» данные за правила, но
/// обязаны нормализовать то, что приходит из формы (пробелы, регистр статуса).
/// </summary>
public class ContractChangeContractsTests
{
    private static ContractChangeInputRequest Request(
        string? organizationId = " org-1 ",
        string? contractId = " contract-1 ",
        string? contractStatus = "IN_EXECUTION",
        string? basisCode = " price_reduction ",
        string? reason = "Снижение цены по соглашению сторон без изменения объёма.",
        DateOnly? changeDate = null,
        string? createdBy = " Иванова А.А. ") =>
        new(
            OrganizationId: organizationId,
            ContractId: contractId,
            ContractNumber: " 0373100000123000001 ",
            ContractName: " Поставка медицинского оборудования ",
            ContractPrice: 100_000m,
            ContractStatus: contractStatus,
            ContractDate: new DateOnly(2026, 9, 1),
            CustomerName: " ГБУЗ ",
            SupplierName: " ООО «Медтехника» ",
            Ikz: " 2612345678901234567890 ",
            EisRegistryNumber: " 0373100000123000001 ",
            ContractQuantity: 100m,
            ContractExecutionEndDate: new DateOnly(2026, 12, 20),
            Kind: ContractChangeKind.Modification,
            BasisCode: basisCode,
            Reason: reason,
            Description: " Изменение условий ",
            ChangeDate: changeDate ?? new DateOnly(2026, 11, 1),
            TerminationDate: null,
            NewPrice: 90_000m,
            NewQuantity: null,
            QuantityUnit: " шт. ",
            NewSubject: null,
            NewSupplier: null,
            NewExecutionEndDate: null,
            NoticeNumber: null,
            NoticeDate: null,
            CourtDecisionNumber: null,
            CourtDecisionDate: null,
            GovernmentActNumber: null,
            GovernmentActDate: null,
            FormulaText: null,
            UnitPrice: null,
            InitialMaxPrice: null,
            CreatedBy: createdBy);

    [Fact]
    public void ToDomain_TrimsStringsAndLowercasesContractStatus()
    {
        var input = Request().ToDomain();

        Assert.Equal("org-1", input.OrganizationId);
        Assert.Equal("contract-1", input.ContractId);
        Assert.Equal("in_execution", input.ContractStatus);
        Assert.Equal("price_reduction", input.BasisCode);
        Assert.Equal("ГБУЗ", input.CustomerName);
        Assert.Equal("шт.", input.QuantityUnit);
        Assert.Equal("Иванова А.А.", input.CreatedBy);
    }

    [Fact]
    public void ToDomain_MissingValues_BecomeEmptyStrings()
    {
        var input = Request(
            organizationId: null,
            contractId: null,
            contractStatus: null,
            basisCode: null,
            reason: null,
            createdBy: null).ToDomain();

        Assert.Equal(string.Empty, input.OrganizationId);
        Assert.Equal(string.Empty, input.ContractId);
        Assert.Equal(string.Empty, input.ContractStatus);
        Assert.Equal(string.Empty, input.BasisCode);
        Assert.Equal(string.Empty, input.Reason);
        Assert.Equal(string.Empty, input.CreatedBy);
    }

    [Fact]
    public void ToDomain_MissingChangeDate_BecomesDefaultDate()
    {
        var input = (Request() with { ChangeDate = null }).ToDomain();

        // Обязательность даты проверяет правило, а не молчаливая подстановка
        // «сегодня»: иначе документ можно было бы сохранить с чужой датой.
        Assert.Equal(default, input.ChangeDate);
    }

    [Fact]
    public void CreateRequest_WithoutInput_Throws()
    {
        var request = new CreateContractChangeRequest(null);

        var error = Assert.Throws<DomainException>(() => request.ToDomain());

        Assert.Contains("Не переданы условия", error.Message);
    }

    [Fact]
    public void UpdateRequest_WithoutInput_Throws()
    {
        var request = new UpdateContractChangeRequest(null, "Иванова А.А.");

        Assert.Throws<DomainException>(() => request.ToDomain());
    }

    [Fact]
    public void Response_ExposesDocumentsIssuesAndAudit()
    {
        var change = ContractChange.Create(ContractChangeSamples.Modification(), ContractChangeSamples.Now);
        change.AddDocument(
            ContractChangeDocumentType.Agreement,
            string.Empty,
            "Соглашение об изменении цены.",
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        var response = ContractChangeResponse.From(change);

        Assert.Equal(change.Id, response.Id);
        Assert.Equal(ContractChangeStatus.Draft, response.Status);
        Assert.Empty(response.Issues.Where(issue => issue.IsError));

        var document = Assert.Single(response.Documents);
        Assert.Equal(ContractChangeDocumentType.Agreement, document.Type);
        Assert.True(document.MatchesHash);
        Assert.Equal("Соглашение", document.Title);

        Assert.Equal(2, response.Events.Count);
        Assert.Equal("Создан", response.Events[0].Action);
        Assert.Equal("Добавлен документ", response.Events[1].Action);
    }

    [Fact]
    public void Response_ForBrokenDraft_ShowsErrorsWithoutThrowing()
    {
        var change = ContractChange.Create(ContractChangeSamples.Modification(), ContractChangeSamples.Now);
        change.UpdateDraft(
            ContractChangeSamples.Modification(contractPrice: 0),
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        var response = ContractChangeResponse.From(change);

        Assert.Contains(response.Issues, issue => issue.IsError && issue.Field == "contractPrice");
    }

    [Fact]
    public void BasisResponse_KeepsLegalFlags()
    {
        var basis = ContractChangeBasisCatalog.Find("quantity_10");
        Assert.NotNull(basis);

        var response = ContractChangeBasisResponse.From(basis!);

        Assert.Equal("quantity_10", response.Code);
        Assert.Equal(ContractChangeKind.Modification, response.Kind);
        Assert.True(response.AllowsQuantityChange);
        Assert.Equal(10m, response.MaxQuantityPercent);
        Assert.Equal(10m, response.MaxPriceIncreasePercent);
        Assert.Contains("ст. 95", response.Article);
    }

    [Fact]
    public void DocumentResponse_ReportsTamperedContent()
    {
        var original = ContractChangeSamples.Document();
        var tampered = ContractChangeDocument.Restore(
            original.Id,
            original.Type,
            original.Title,
            "Изменённый текст",
            original.Version,
            original.Sha256,
            original.CreatedBy,
            original.CreatedAt);

        var response = ContractChangeDocumentResponse.From(tampered);

        Assert.False(response.MatchesHash);
    }
}
