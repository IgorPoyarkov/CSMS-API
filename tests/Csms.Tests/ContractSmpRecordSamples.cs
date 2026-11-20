using System.Globalization;
using Csms.Api;
using Csms.Domain;

namespace Csms.Tests;

/// <summary>Готовые входные данные для тестов attracting СМП/СОНКО.</summary>
internal static class ContractSmpRecordSamples
{
    public static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-06T09:20:00Z", CultureInfo.InvariantCulture);

    public const string Actor = "Иванова А. С., контрактная служба";
    public const string SupplierActor = "Григорьев А. Н., директор ООО «РосДорМонтаж»";
    public const string ContractId = "contract-1";

    public static ContractSmpRecordInput SubContract() => new(
        OrganizationId: "1",
        ContractId: ContractId,
        ContractNumber: "0344200012126000001",
        ContractName: "Устройство бордюрного камня",
        ContractPrice: 32_000_000m,
        RequiredPercent: 10m,
        EisRegistryNumber: "0344200012126000001",
        DocumentType: ContractSmpDocumentType.SubContract,
        DocumentNumber: "5",
        DocumentDate: new DateOnly(2026, 10, 5),
        Amount: 1_200_000m,
        Okpd2Code: "43.31.10",
        PriceType: ContractSmpPriceType.Contract,
        UnitPrice: null,
        Subject: "Устройство бордюрного камня на участке 1",
        Period: "2026-10",
        SupplierName: "ООО «РосДорМонтаж»",
        SupplierInn: "3123001111",
        SupplierKpp: "312301001",
        IsSmpSubject: true,
        SubContractRecordId: null,
        IsReplacementSubContract: false,
        PaidEarlierThanContractSchedule: false,
        PaidToSupplierDate: null,
        AcceptanceSignedDate: null,
        ReceivedAt: Now,
        SourceFileName: "Договор №5 от 05.10.2026.pdf",
        SupplierConfirmedAt: Now,
        SupplierConfirmedBy: SupplierActor,
        SupplierConfirmedDocument: "сопроводительное письмо от 06.10.2026",
        CustomerConfirmedAt: Now.AddDays(1),
        CustomerConfirmedBy: Actor,
        CustomerConfirmedDocument: "проверка по реестру МСП от 07.10.2026",
        EisPublishDate: null,
        EisRecordNumber: null,
        Comment: "Договор загружен подрядчиком",
        CreatedBy: SupplierActor);

    /// <summary>Документ-основание со ссылкой на договор субподрядчика.</summary>
    public static ContractSmpRecordInput SupportingDocument(ContractSmpDocumentType type, string recordId) =>
        SubContract() with
        {
            DocumentType = type,
            DocumentNumber = type == ContractSmpDocumentType.Declaration ? "12/СМП" : "18",
            DocumentDate = new DateOnly(2026, 10, 8),
            Amount = null,
            Subject = null,
            SubContractRecordId = recordId,
            SourceFileName = string.Empty,
        };

    /// <summary>Запрос API в том виде, как его шлёт frontend (все поля строками).</summary>
    public static ContractSmpRecordInputRequest Request() =>
        new(
            OrganizationId: "1",
            ContractId: ContractId,
            ContractNumber: "0344200012126000001",
            ContractName: "Устройство бордюрного камня",
            ContractPrice: "32000000",
            RequiredPercent: "10",
            EisRegistryNumber: "0344200012126000001",
            DocumentType: ContractSmpDocumentType.SubContract,
            DocumentNumber: "5",
            DocumentDate: "2026-10-05",
            Amount: "1200000",
            Okpd2Code: "43.31.10",
            PriceType: ContractSmpPriceType.Contract,
            UnitPrice: "",
            Subject: "Устройство бордюрного камня на участке 1",
            Period: "2026-10",
            SupplierName: "ООО «РосДорМонтаж»",
            SupplierInn: "3123001111",
            SupplierKpp: "312301001",
            IsSmpSubject: true,
            SubContractRecordId: "",
            IsReplacementSubContract: false,
            PaidEarlierThanContractSchedule: false,
            PaidToSupplierDate: "",
            AcceptanceSignedDate: "",
            ReceivedAt: "2026-10-06T09:20:00Z",
            SourceFileName: "Договор №5 от 05.10.2026.pdf",
            SupplierConfirmedAt: "2026-10-06T09:20:00Z",
            SupplierConfirmedBy: SupplierActor,
            SupplierConfirmedDocument: "сопроводительное письмо от 06.10.2026",
            CustomerConfirmedAt: "2026-10-07T11:00:00Z",
            CustomerConfirmedBy: Actor,
            CustomerConfirmedDocument: "проверка по реестру МСП от 07.10.2026",
            EisPublishDate: "",
            EisRecordNumber: "",
            Comment: "Договор загружен подрядчиком",
            Actor: SupplierActor);
}
