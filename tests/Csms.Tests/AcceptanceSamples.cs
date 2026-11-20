using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Образцы условий приёмки для тестов.
///
/// Один раз описывается корректный снимок исполняемого контракта, а конкретный
/// тест меняет только то поле, которое проверяет: падение теста указывает на
/// правило, а не на забытый обязательный реквизит.
/// </summary>
internal static class AcceptanceSamples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly ContractDate = new(2026, 9, 1);
    public static readonly DateOnly DocumentDate = new(2026, 11, 10);

    /// <summary>Автор документа (поставщик).</summary>
    public const string Actor = "Иванова А.А.";

    /// <summary>Заказчик, подписывающий итог приёмки.</summary>
    public const string Customer = "Петров П.П.";

    public const string RegistryNumber = "0373100000123000001";

    public static AcceptanceInput Primary(
        decimal contractPrice = 100_000m,
        string contractStatus = "in_execution",
        string? documentNumber = "АКТ-1",
        DateOnly? documentDate = default,
        int acceptanceTermDays = 10,
        bool expertiseRequired = true,
        bool expertiseByThirdParty = false,
        bool commissionRequired = false,
        AcceptanceKind kind = AcceptanceKind.Primary,
        string? correctedDocumentNumber = null,
        DateOnly? correctedDocumentDate = null,
        string? correctionReason = null,
        string? eisRegistryNumber = RegistryNumber,
        int? stageNumber = 1,
        string createdBy = Actor) =>
        new(
            OrganizationId: "org-1",
            ContractId: "contract-1",
            ContractNumber: RegistryNumber,
            ContractName: "Поставка медицинского оборудования",
            ContractPrice: contractPrice,
            ContractStatus: contractStatus,
            ContractDate: ContractDate,
            CustomerName: "ГБУЗ «Городская больница»",
            SupplierName: "ООО «Медтехника»",
            SupplierInn: "7701234567",
            Ikz: "2612345678901234567890",
            EisRegistryNumber: eisRegistryNumber,
            StageNumber: stageNumber,
            StageName: "Этап 1",
            Kind: kind,
            DocumentNumber: documentNumber,
            DocumentDate: documentDate == default ? DocumentDate : documentDate,
            CorrectedDocumentNumber: correctedDocumentNumber,
            CorrectedDocumentDate: correctedDocumentDate,
            CorrectionReason: correctionReason,
            AcceptanceTermDays: acceptanceTermDays,
            ExpertiseRequired: expertiseRequired,
            ExpertiseByThirdParty: expertiseByThirdParty,
            ExpertiseOrganization: null,
            ExpertiseConclusionNumber: null,
            ExpertiseConclusionDate: null,
            CommissionRequired: commissionRequired,
            CommissionDecisionNumber: null,
            CommissionDecisionDate: null,
            DeliveryPlace: "г. Курск, склад заказчика",
            Notes: null,
            CreatedBy: createdBy);

    /// <summary>Корректировочный документ к ранее оформленной приёмке.</summary>
    public static AcceptanceInput Correction(
        string? correctionReason = "Исправление количества по позиции № 1.",
        string? correctedDocumentNumber = "АКТ-1",
        DateOnly? correctedDocumentDate = null) =>
        Primary(
            kind: AcceptanceKind.Correction,
            correctedDocumentNumber: correctedDocumentNumber,
            correctedDocumentDate: correctedDocumentDate ?? DocumentDate,
            correctionReason: correctionReason);

    public static AcceptancePosition Position(
        string name = "Шприц 5 мл",
        decimal quantity = 100m,
        decimal unitPrice = 200m,
        decimal vatRate = 20m,
        string unit = "шт.",
        string? okpd2 = "32.50.13.190") =>
        AcceptancePosition.Create(name, okpd2, unit, quantity, unitPrice, vatRate, null, null);

    public static AcceptanceDefect Defect(
        Guid? positionId = null,
        string description = "Поставлено 5 шприцев с истёкшим сроком годности.",
        decimal quantity = 5m,
        AcceptanceDefectRequirement requirement = AcceptanceDefectRequirement.Replace) =>
        AcceptanceDefect.Create(positionId, description, quantity, requirement);

    /// <summary>Черновик без позиций: сохраняется, пока документ не направлен.</summary>
    public static ContractAcceptance Draft(AcceptanceInput? input = null) =>
        ContractAcceptance.Create(input ?? Primary(), Now);

    /// <summary>Черновик с одной позицией.</summary>
    public static ContractAcceptance DraftWithPosition(
        AcceptanceInput? input = null,
        decimal quantity = 100m,
        decimal unitPrice = 200m,
        decimal vatRate = 20m)
    {
        var acceptance = Draft(input);

        acceptance.AddPosition(
            "Шприц 5 мл",
            "32.50.13.190",
            "шт.",
            quantity,
            unitPrice,
            vatRate,
            null,
            null,
            Actor,
            Now);

        return acceptance;
    }

    /// <summary>Документ, направленный заказчику: позиции уже зафиксированы.</summary>
    public static ContractAcceptance Submitted(AcceptanceInput? input = null)
    {
        var acceptance = DraftWithPosition(input);
        acceptance.Submit(Actor, Now);

        return acceptance;
    }

    /// <summary>Принято без расхождений.</summary>
    public static ContractAcceptance Accepted(AcceptanceInput? input = null)
    {
        var acceptance = Submitted(input);
        acceptance.Accept(AcceptanceResult.Accepted, Customer, null, Now);

        return acceptance;
    }
}
