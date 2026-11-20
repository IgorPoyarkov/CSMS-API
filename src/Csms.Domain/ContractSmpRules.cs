using System.Text.RegularExpressions;

namespace Csms.Domain;

/// <summary>Замечание проверки: блокирующая ошибка или предупреждение.</summary>
public sealed record ContractSmpIssue(string Severity, string Field, string Message)
{
    public bool IsError => Severity == "error";
}

/// <summary>
/// Входные данные записи о привлечении СМП/СОНКО. Как и в модуле изменений
/// контракта, снимок контракта приходит с фронта: API не читает json-server и
/// не подменяет цену или процент привлечения «своими» значениями.
/// </summary>
public sealed record ContractSmpRecordInput(
    string OrganizationId,
    string ContractId,
    string ContractNumber,
    string ContractName,
    decimal ContractPrice,
    decimal? RequiredPercent,
    string? EisRegistryNumber,
    ContractSmpDocumentType DocumentType,
    string DocumentNumber,
    DateOnly DocumentDate,
    decimal? Amount,
    string? Okpd2Code,
    ContractSmpPriceType PriceType,
    decimal? UnitPrice,
    string? Subject,
    string? Period,
    string SupplierName,
    string? SupplierInn,
    string? SupplierKpp,
    bool IsSmpSubject,
    string? SubContractRecordId,
    bool IsReplacementSubContract,
    bool PaidEarlierThanContractSchedule,
    DateOnly? PaidToSupplierDate,
    DateOnly? AcceptanceSignedDate,
    DateTimeOffset ReceivedAt,
    string? SourceFileName,
    DateTimeOffset? SupplierConfirmedAt,
    string? SupplierConfirmedBy,
    string? SupplierConfirmedDocument,
    DateTimeOffset? CustomerConfirmedAt,
    string? CustomerConfirmedBy,
    string? CustomerConfirmedDocument,
    DateOnly? EisPublishDate,
    string? EisRecordNumber,
    string? Comment,
    string CreatedBy);

/// <summary>
/// Проверки сведений о привлечении СМП/СОНКО: ч. 5-6 ст. 30 44-ФЗ,
/// п. 1-6 типовых условий (ПП РФ от 23.12.2016 N 1466) и реквизиты ЕИС
/// по карте XSD 17.0. Те же правила работают в форме (POST /validate), при
/// сохранении и при каждом переходе статуса.
/// </summary>
public static class ContractSmpRules
{
    private static readonly Regex InnPattern = new(@"^\d{10}(\d{2})?$", RegexOptions.Compiled);
    private static readonly Regex KppPattern = new(@"^\d{9}$", RegexOptions.Compiled);
    private static readonly Regex Okpd2Pattern = new(@"^\d{2}(\.\d+)*$", RegexOptions.Compiled);
    private static readonly Regex EisRegistryPattern = new(@"^\d{19}$", RegexOptions.Compiled);

    public static IReadOnlyList<ContractSmpIssue> Validate(
        ContractSmpRecordInput input,
        IReadOnlyList<ContractSmpDocument> documents,
        ContractSmpStatus status,
        DateTimeOffset? now = null)
    {
        var issues = new List<ContractSmpIssue>();
        var moment = now ?? DateTimeOffset.UtcNow;

        void Error(string field, string message) =>
            issues.Add(new ContractSmpIssue("error", field, message));

        void Warning(string field, string message) =>
            issues.Add(new ContractSmpIssue("warning", field, message));

        // ─── Контракт: снимок обязателен, даже если карточку потом удалят ───
        if (string.IsNullOrWhiteSpace(input.OrganizationId))
        {
            Error("organizationId", "Не указана организация заказчика.");
        }

        if (string.IsNullOrWhiteSpace(input.ContractId))
        {
            Error("contractId", "Не указан контракт: сведения должны быть привязаны к реестровой записи.");
        }

        if (string.IsNullOrWhiteSpace(input.ContractNumber))
        {
            Error("contractNumber", "Не указан номер контракта.");
        }

        if (string.IsNullOrWhiteSpace(input.ContractName))
        {
            Error("contractName", "Не указано наименование контракта.");
        }

        if (input.ContractPrice <= 0)
        {
            Error("contractPrice", "Цена контракта должна быть больше нуля.");
        }

        if (input.RequiredPercent is null)
        {
            Warning(
                "requiredPercent",
                "Процент привлечения не указан: проверить выполнение объёма автоматически нельзя.");
        }
        else if (input.RequiredPercent is < 0 or > 100)
        {
            Error("requiredPercent", "Процент привлечения должен быть от 0 до 100.");
        }

        // ─── Реквизиты договора с субподрядчиком ────────────────────────────
        if (string.IsNullOrWhiteSpace(input.DocumentNumber) || input.DocumentNumber.Trim().Length > 100)
        {
            Error("documentNumber", "Укажите номер документа (не более 100 символов).");
        }

        if (input.DocumentDate == default)
        {
            Error("documentDate", "Укажите дату документа.");
        }
        else if (input.DocumentDate > DateOnly.FromDateTime(moment.UtcDateTime.Date))
        {
            Error("documentDate", "Дата документа не может быть в будущем.");
        }

        if (input.Amount is null || input.Amount <= 0)
        {
            if (input.DocumentType == ContractSmpDocumentType.SubContract)
            {
                Error("amount", "Укажите сумму договора с субподрядчиком: она закрывает объём привлечения.");
            }
            else if (input.Amount is not null)
            {
                Error("amount", "Сумма документа должна быть больше нуля.");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.Okpd2Code) &&
            !Okpd2Pattern.IsMatch(input.Okpd2Code.Trim()))
        {
            Error("okpd2Code", "ОКПД2: цифры и точки, например 43.31.10.");
        }
        else if (string.IsNullOrWhiteSpace(input.Okpd2Code) &&
                 status is ContractSmpStatus.Accepted or ContractSmpStatus.PushedToEis)
        {
            Warning("okpd2Code", "Код ОКПД2 не указан: карточка ЕИС будет неполной.");
        }

        if (input.PriceType == ContractSmpPriceType.Unit && (input.UnitPrice is null || input.UnitPrice <= 0))
        {
            Error("unitPrice", "При виде цены «цена единицы» укажите цену больше нуля.");
        }

        if (input.Subject?.Length > 500)
        {
            Error("subject", "Предмет договора не может быть длиннее 500 символов.");
        }

        if (input.Period?.Length > 100)
        {
            Error("period", "Период не может быть длиннее 100 символов.");
        }

        // ─── Субподрядчик: ИНН/КПП сверяются с картой subContractorsnfo ─────
        if (string.IsNullOrWhiteSpace(input.SupplierName) || input.SupplierName.Trim().Length < 3)
        {
            Error("supplierName", "Укажите наименование субподрядчика (не менее 3 символов).");
        }
        else if (input.SupplierName.Trim().Length > 512)
        {
            Error("supplierName", "Наименование субподрядчика не может быть длиннее 512 символов.");
        }

        var inn = input.SupplierInn?.Trim();

        if (!string.IsNullOrWhiteSpace(inn) && !InnPattern.IsMatch(inn))
        {
            Error("supplierInn", "ИНН — 10 или 12 цифр.");
        }

        if (!string.IsNullOrWhiteSpace(input.SupplierKpp) &&
            !KppPattern.IsMatch(input.SupplierKpp.Trim()))
        {
            Error("supplierKpp", "КПП — 9 цифр.");
        }

        if (!input.IsSmpSubject)
        {
            Warning(
                "isSmpSubject",
                "Субподрядчик не отмечен как СМП/СОНКО: договор не закрывает объём привлечения.");
        }

        // ─── Сроки и даты типовых условий ───────────────────────────────────
        if (input.ReceivedAt == default)
        {
            Error("receivedAt", "Зафиксируйте дату и время получения сведений от подрядчика.");
        }
        else if (input.ReceivedAt > moment)
        {
            Error("receivedAt", "Дата получения сведений не может быть в будущем.");
        }
        else if (input.DocumentDate > DateOnly.FromDateTime(input.ReceivedAt.UtcDateTime))
        {
            Error("documentDate", "Дата документа не может быть позже даты получения сведений.");
        }

        if (input.PaidToSupplierDate is not null)
        {
            if (input.PaidToSupplierDate < input.DocumentDate)
            {
                Error("paidToSupplierDate", "Дата оплаты не может быть раньше даты договора.");
            }
            else if (input.PaidToSupplierDate > DateOnly.FromDateTime(input.ReceivedAt.UtcDateTime))
            {
                Error("paidToSupplierDate", "Дата оплаты не может быть позже даты получения сведений.");
            }
        }

        if (input.AcceptanceSignedDate is not null &&
            input.AcceptanceSignedDate > DateOnly.FromDateTime(input.ReceivedAt.UtcDateTime))
        {
            Error("acceptanceSignedDate", "Дата подписания акта не может быть позже даты получения сведений.");
        }

        if (input.IsReplacementSubContract && input.DocumentType != ContractSmpDocumentType.SubContract)
        {
            Warning(
                "isReplacementSubContract",
                "Признак замены субподрядчика имеет смысл только для договора с субподрядчиком.");
        }

        // ─── Документы-основания привязываются к договору ───────────────────
        var isSupportingDocument = input.DocumentType is ContractSmpDocumentType.Declaration
            or ContractSmpDocumentType.Acceptance
            or ContractSmpDocumentType.Payment;

        if (isSupportingDocument && string.IsNullOrWhiteSpace(input.SubContractRecordId))
        {
            if (status is ContractSmpStatus.Accepted or ContractSmpStatus.PushedToEis)
            {
                Error(
                    "subContractRecordId",
                    "Укажите договор субподрядчика, к которому относится этот документ.");
            }
            else
            {
                Warning(
                    "subContractRecordId",
                    "Документ не привязан к договору субподрядчика: сроки будут считаться по ИНН.");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.SourceFileName) && input.SourceFileName.Length > 255)
        {
            Error("sourceFileName", "Имя файла не может быть длиннее 255 символов.");
        }

        if (input.Comment?.Length > 1000)
        {
            Error("comment", "Комментарий не может быть длиннее 1000 символов.");
        }

        // ─── Подтверждения достоверности (п. 6 типовых условий) ─────────────
        ValidateConfirmation(
            "supplierConfirmed",
            input.SupplierConfirmedAt,
            input.SupplierConfirmedBy,
            input.SupplierConfirmedDocument,
            status is ContractSmpStatus.Accepted or ContractSmpStatus.PushedToEis,
            Error);

        ValidateConfirmation(
            "customerConfirmed",
            input.CustomerConfirmedAt,
            input.CustomerConfirmedBy,
            input.CustomerConfirmedDocument,
            status is ContractSmpStatus.Accepted or ContractSmpStatus.PushedToEis,
            Error);

        // ─── Выгрузка в ЕИС: реквизиты карточки реестра контрактов ──────────
        if (status == ContractSmpStatus.PushedToEis)
        {
            if (string.IsNullOrWhiteSpace(input.EisRegistryNumber) ||
                !EisRegistryPattern.IsMatch(input.EisRegistryNumber.Trim()))
            {
                Error(
                    "eisRegistryNumber",
                    "Укажите 19-значный реестровый номер контракта ЕИС.");
            }

            if (string.IsNullOrWhiteSpace(inn))
            {
                Error("supplierInn", "Для выгрузки в ЕИС укажите ИНН субподрядчика.");
            }
            else if (inn.Length == 10 && string.IsNullOrWhiteSpace(input.SupplierKpp))
            {
                Error("supplierKpp", "Для юридического лица укажите КПП из карточки ЕИС.");
            }

            if (string.IsNullOrWhiteSpace(input.Okpd2Code))
            {
                Error("okpd2Code", "Для выгрузки в ЕИС укажите код ОКПД2.");
            }

            if (input.EisPublishDate is null)
            {
                Error("eisPublishDate", "Укажите дату выгрузки сведений в ЕИС.");
            }
            else if (input.EisPublishDate < input.DocumentDate)
            {
                Error("eisPublishDate", "Дата выгрузки не может быть раньше даты документа.");
            }

            if (string.IsNullOrWhiteSpace(input.EisRecordNumber))
            {
                Error("eisRecordNumber", "Укажите номер записи, присвоенный ЕИС.");
            }
        }

        // Подписанный или выгруженный комплект не должен расходиться с хешем
        foreach (var document in documents.Where(document => !document.MatchesHash()))
        {
            Error(
                "documents",
                $"Документ «{document.Title}» изменён после сохранения: хеш содержимого не совпадает.");
        }

        return issues;
    }

    private static void ValidateConfirmation(
        string field,
        DateTimeOffset? at,
        string? by,
        string? document,
        bool required,
        Action<string, string> error)
    {
        if (at is null)
        {
            if (required)
            {
                error(field, $"Зафиксируйте подтверждение: {field} не содержит даты и времени.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(by))
        {
            error(field, $"Укажите, кто подтвердил достоверность ({field}).");
        }

        if (string.IsNullOrWhiteSpace(document))
        {
            error(field, $"Укажите документ-основание подтверждения ({field}).");
        }
    }
}
