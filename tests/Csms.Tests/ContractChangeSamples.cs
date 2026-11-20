using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Образцы условий изменения и расторжения для тестов.
///
/// Один раз описывается корректный снимок исполняемого контракта, а конкретный
/// тест меняет только то поле, которое проверяет. Так падение теста указывает
/// на правило, а не на забытый обязательный реквизит.
/// </summary>
internal static class ContractChangeSamples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly ContractDate = new(2026, 9, 1);
    public static readonly DateOnly ChangeDate = new(2026, 11, 1);
    public static readonly DateOnly ContractEndDate = new(2026, 12, 20);

    public const string Actor = "Иванова А.А.";
    public const string Approver = "Петров П.П.";
    public const string RegistryNumber = "0373100000123000001";

    public static ContractChangeInput Modification(
        string basisCode = "price_reduction",
        decimal? newPrice = 90_000m,
        decimal? newQuantity = null,
        string? newSubject = null,
        string? newSupplier = null,
        string? governmentActNumber = null,
        DateOnly? governmentActDate = null,
        string? formulaText = null,
        decimal? unitPrice = null,
        decimal? initialMaxPrice = null,
        DateOnly? newExecutionEndDate = null,
        DateOnly? terminationDate = null,
        string contractStatus = "in_execution",
        decimal contractPrice = 100_000m,
        decimal? contractQuantity = 100m,
        string? reason = null,
        DateOnly? changeDate = null,
        string? eisRegistryNumber = RegistryNumber,
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
            Ikz: "2612345678901234567890",
            EisRegistryNumber: eisRegistryNumber,
            ContractQuantity: contractQuantity,
            ContractExecutionEndDate: ContractEndDate,
            Kind: ContractChangeKind.Modification,
            BasisCode: basisCode,
            Reason: reason ?? "Снижение цены по соглашению сторон без изменения объёма и предмета контракта.",
            Description: "Изменение существенных условий",
            ChangeDate: changeDate ?? ChangeDate,
            TerminationDate: terminationDate,
            NewPrice: newPrice,
            NewQuantity: newQuantity,
            QuantityUnit: "шт.",
            NewSubject: newSubject,
            NewSupplier: newSupplier,
            NewExecutionEndDate: newExecutionEndDate,
            NoticeNumber: null,
            NoticeDate: null,
            CourtDecisionNumber: null,
            CourtDecisionDate: null,
            GovernmentActNumber: governmentActNumber,
            GovernmentActDate: governmentActDate,
            FormulaText: formulaText,
            UnitPrice: unitPrice,
            InitialMaxPrice: initialMaxPrice,
            CreatedBy: createdBy);

    public static ContractChangeInput Termination(
        string basisCode = "by_agreement",
        DateOnly? terminationDate = null,
        string? noticeNumber = null,
        DateOnly? noticeDate = null,
        string? courtDecisionNumber = null,
        DateOnly? courtDecisionDate = null,
        decimal? newPrice = null,
        string contractStatus = "in_execution",
        string? eisRegistryNumber = RegistryNumber,
        string? reason = null,
        DateOnly? changeDate = null,
        string createdBy = Actor) =>
        new(
            OrganizationId: "org-1",
            ContractId: "contract-1",
            ContractNumber: RegistryNumber,
            ContractName: "Поставка медицинского оборудования",
            ContractPrice: 100_000m,
            ContractStatus: contractStatus,
            ContractDate: ContractDate,
            CustomerName: "ГБУЗ «Городская больница»",
            SupplierName: "ООО «Медтехника»",
            Ikz: "2612345678901234567890",
            EisRegistryNumber: eisRegistryNumber,
            ContractQuantity: 100m,
            ContractExecutionEndDate: ContractEndDate,
            Kind: ContractChangeKind.Termination,
            BasisCode: basisCode,
            Reason: reason ?? "Стороны согласовали прекращение обязательств по контракту без взаимных претензий.",
            Description: "Расторжение по соглашению сторон",
            ChangeDate: changeDate ?? ChangeDate,
            TerminationDate: terminationDate ?? new DateOnly(2026, 11, 25),
            NewPrice: newPrice,
            NewQuantity: null,
            QuantityUnit: null,
            NewSubject: null,
            NewSupplier: null,
            NewExecutionEndDate: null,
            NoticeNumber: noticeNumber,
            NoticeDate: noticeDate,
            CourtDecisionNumber: courtDecisionNumber,
            CourtDecisionDate: courtDecisionDate,
            GovernmentActNumber: null,
            GovernmentActDate: null,
            FormulaText: null,
            UnitPrice: null,
            InitialMaxPrice: null,
            CreatedBy: createdBy);

    /// <summary>Документ нужного вида с осмысленным текстом.</summary>
    public static ContractChangeDocument Document(
        ContractChangeDocumentType type = ContractChangeDocumentType.Agreement,
        string content = "Текст соглашения об изменении условий контракта.") =>
        ContractChangeDocument.Create(type, string.Empty, content, Actor, Now);
}

/// <summary>Часы с управляемым «сейчас»: сценарии не зависят от реального времени.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    /// <summary>Сдвинуть время вперёд, чтобы порядок событий был различим.</summary>
    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}
