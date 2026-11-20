using Csms.Api;
using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Контракты HTTP-слоя: DTO не должны «додумывать» данные за правила, но
/// обязаны нормализовать то, что приходит из формы (пробелы, регистр статуса),
/// и отдавать фронту итоги вместе с замечаниями.
/// </summary>
public class AcceptanceContractsTests
{
    private static AcceptanceInputRequest Request(
        string? organizationId = " org-1 ",
        string? contractId = " contract-1 ",
        string? contractStatus = "IN_EXECUTION",
        string? documentNumber = " АКТ-1 ",
        int? acceptanceTermDays = 10,
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
            SupplierInn: " 7701234567 ",
            Ikz: " 2612345678901234567890 ",
            EisRegistryNumber: " 0373100000123000001 ",
            StageNumber: 1,
            StageName: " Этап 1 ",
            Kind: AcceptanceKind.Primary,
            DocumentNumber: documentNumber,
            DocumentDate: new DateOnly(2026, 11, 10),
            CorrectedDocumentNumber: null,
            CorrectedDocumentDate: null,
            CorrectionReason: null,
            AcceptanceTermDays: acceptanceTermDays,
            ExpertiseRequired: true,
            ExpertiseByThirdParty: false,
            ExpertiseOrganization: null,
            ExpertiseConclusionNumber: null,
            ExpertiseConclusionDate: null,
            CommissionRequired: false,
            CommissionDecisionNumber: null,
            CommissionDecisionDate: null,
            DeliveryPlace: " склад заказчика ",
            Notes: null,
            CreatedBy: createdBy);

    [Fact]
    public void ToDomain_TrimsStringsAndLowercasesContractStatus()
    {
        var input = Request().ToDomain();

        Assert.Equal("org-1", input.OrganizationId);
        Assert.Equal("contract-1", input.ContractId);
        Assert.Equal("in_execution", input.ContractStatus);
        Assert.Equal("АКТ-1", input.DocumentNumber);
        Assert.Equal("ГБУЗ", input.CustomerName);
        Assert.Equal("7701234567", input.SupplierInn);
        Assert.Equal("склад заказчика", input.DeliveryPlace);
        Assert.Equal("Иванова А.А.", input.CreatedBy);
    }

    [Fact]
    public void ToDomain_MissingTerm_FallsBackToLegalMaximum()
    {
        var input = Request(acceptanceTermDays: null).ToDomain();

        // Если срок не передан, берём предельный по ч. 13 ст. 94: правило
        // проверит его как обычное значение контракта.
        Assert.Equal(AcceptanceRules.MaxReviewTermDays, input.AcceptanceTermDays);
    }

    [Fact]
    public void ToDomain_MissingValues_BecomeEmptyStrings()
    {
        var input = Request(organizationId: null, contractId: null, contractStatus: null, createdBy: null)
            .ToDomain();

        Assert.Equal(string.Empty, input.OrganizationId);
        Assert.Equal(string.Empty, input.ContractId);
        Assert.Equal(string.Empty, input.ContractStatus);
        Assert.Equal(string.Empty, input.CreatedBy);
    }

    [Fact]
    public void CreateRequest_WithoutInput_Throws()
    {
        var request = new CreateAcceptanceRequest(null);

        var error = Assert.Throws<DomainException>(() => request.ToDomain());

        Assert.Contains("Не переданы условия приёмки", error.Message);
    }

    [Fact]
    public void UpdateRequest_WithoutInput_Throws()
    {
        var request = new UpdateAcceptanceRequest(null, "Иванова А.А.");

        Assert.Throws<DomainException>(() => request.ToDomain());
    }

    [Fact]
    public void Response_ExposesPositionsTotalsAndDeadline()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var response = AcceptanceResponse.From(acceptance);

        Assert.Equal(acceptance.Id, response.Id);
        Assert.Equal(AcceptanceStatus.Submitted, response.Status);
        Assert.Null(response.Result);
        Assert.DoesNotContain(response.Issues, issue => issue.IsError);

        var position = Assert.Single(response.Positions);
        Assert.Equal(20_000m, position.TotalWithVat);
        Assert.Equal(3_333.33m, position.VatAmount);

        Assert.Equal(20_000m, response.TotalWithVat);
        Assert.Equal(3_333.33m, response.VatAmount);
        Assert.Equal(new DateOnly(2026, 11, 24), response.ReviewDeadline);

        Assert.Equal(3, response.Events.Count);
        Assert.Equal("Создан", response.Events[0].Action);
        Assert.Equal("Добавлена позиция", response.Events[1].Action);
        Assert.Equal("Направлен заказчику", response.Events[2].Action);
    }

    [Fact]
    public void Response_ForBrokenDraft_ShowsErrorsWithoutThrowing()
    {
        var acceptance = AcceptanceSamples.Draft();
        acceptance.UpdateDraft(
            AcceptanceSamples.Primary(contractPrice: 0),
            AcceptanceSamples.Actor,
            AcceptanceSamples.Now);

        var response = AcceptanceResponse.From(acceptance);

        Assert.Contains(response.Issues, issue => issue.IsError && issue.Field == "contractPrice");
    }

    [Fact]
    public void DefectResponse_KeepsRequirementLabel()
    {
        var defect = AcceptanceSamples.Defect();

        var response = AcceptanceDefectResponse.From(defect);

        Assert.Equal(AcceptanceDefectRequirement.Replace, response.Requirement);
        Assert.Equal("Заменить", response.RequirementLabel);
        Assert.Equal(defect.Description, response.Description);
    }

    [Fact]
    public void AcceptedResponse_HasEisResultCode()
    {
        var acceptance = AcceptanceSamples.Accepted();

        var response = AcceptanceResponse.From(acceptance);

        Assert.Equal(AcceptanceResult.Accepted, response.Result);
        Assert.Equal(1, (int)response.Result!.Value);
        Assert.Equal(AcceptanceSamples.Customer, response.SignedBy);
    }
}
