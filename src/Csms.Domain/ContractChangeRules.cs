namespace Csms.Domain;

/// <summary>Замечание проверки: блокирующая ошибка или предупреждение.</summary>
public sealed record ContractChangeIssue(string Severity, string Field, string Message)
{
    public bool IsError => Severity == "error";
}

/// <summary>
/// Входные данные изменения/расторжения — снимок контракта и предлагаемые
/// условия. Снимок приходит с фронта из реестра контрактов: модуль не читает
/// json-server и не подменяет цену или статус контракта «своими».
/// </summary>
public sealed record ContractChangeInput(
    string OrganizationId,
    string ContractId,
    string ContractNumber,
    string ContractName,
    decimal ContractPrice,
    string ContractStatus,
    DateOnly? ContractDate,
    string? CustomerName,
    string? SupplierName,
    string? Ikz,
    string? EisRegistryNumber,
    decimal? ContractQuantity,
    DateOnly? ContractExecutionEndDate,
    ContractChangeKind Kind,
    string BasisCode,
    string Reason,
    string? Description,
    DateOnly ChangeDate,
    DateOnly? TerminationDate,
    decimal? NewPrice,
    decimal? NewQuantity,
    string? QuantityUnit,
    string? NewSubject,
    string? NewSupplier,
    DateOnly? NewExecutionEndDate,
    string? NoticeNumber,
    DateOnly? NoticeDate,
    string? CourtDecisionNumber,
    DateOnly? CourtDecisionDate,
    string? GovernmentActNumber,
    DateOnly? GovernmentActDate,
    string? FormulaText,
    decimal? UnitPrice,
    decimal? InitialMaxPrice,
    string CreatedBy);

/// <summary>
/// Проверки допустимости изменения и расторжения по ст. 95 44-ФЗ.
///
/// Это не «подсказки в форме»: те же правила вызываются при сохранении,
/// отправке на согласование, подписании и регистрации в ЕИС. Обойти их из
/// интерфейса нельзя.
/// </summary>
public static class ContractChangeRules
{
    private static readonly string[] ChangeableContractStatuses =
        ["signed", "in_execution", "suspended"];

    /// <summary>Срок вступления в силу одностороннего отказа, ст. 95 ч. 13.</summary>
    public const int UnilateralNoticeDays = 10;

    public static IReadOnlyList<ContractChangeIssue> Validate(
        ContractChangeInput input,
        IReadOnlyList<ContractChangeDocument> documents,
        ContractChangeStatus status)
    {
        var issues = new List<ContractChangeIssue>();
        var basis = ContractChangeBasisCatalog.Find(input.BasisCode);

        void Error(string field, string message) =>
            issues.Add(new ContractChangeIssue("error", field, message));

        void Warning(string field, string message) =>
            issues.Add(new ContractChangeIssue("warning", field, message));

        // ─── Контракт и общие реквизиты ─────────────────────────────────────
        if (string.IsNullOrWhiteSpace(input.OrganizationId))
        {
            Error("organizationId", "Не указана организация заказчика.");
        }

        if (string.IsNullOrWhiteSpace(input.ContractId))
        {
            Error("contractId", "Не указан контракт: изменение должно быть привязано к реестровой записи.");
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

        if (!ChangeableContractStatuses.Contains(input.ContractStatus))
        {
            Error(
                "contractStatus",
                input.ContractStatus is "completed" or "terminated"
                    ? "Контракт уже завершён или расторгнут: изменить или расторгнуть его повторно нельзя."
                    : "Изменять и расторгать можно только заключённый или исполняемый контракт.");
        }

        if (input.ContractDate is null)
        {
            Error("contractDate", "Не указана дата заключения контракта.");
        }

        if (basis is null)
        {
            Error("basisCode", "Неизвестное основание изменения или расторжения.");
        }
        else if (basis.Kind != input.Kind)
        {
            Error("basisCode", "Основание не соответствует виду документа: изменение и расторжение не смешиваются.");
        }

        if (input.ChangeDate < input.ContractDate)
        {
            Error("changeDate", "Дата документа не может быть раньше даты заключения контракта.");
        }

        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length < 10)
        {
            Error("reason", "Опишите причину изменения или расторжения (не менее 10 символов).");
        }

        if (basis is not null)
        {
            if (input.Kind == ContractChangeKind.Modification)
            {
                ValidateModification(input, basis, Error, Warning);
            }
            else
            {
                ValidateTermination(input, basis, issues, Error, Warning);
            }

            ValidateDocuments(input, basis, documents, status, Error);
            ValidateEis(input, basis, status, Error);
        }

        // Документы с расхождением хеша — признак правки после согласования
        foreach (var document in documents.Where(document => !document.MatchesHash()))
        {
            Error(
                "documents",
                $"Документ «{document.Title}» изменён после сохранения: хеш содержимого не совпадает. Сформируйте новую редакцию.");
        }

        return issues;
    }

    private static void ValidateModification(
        ContractChangeInput input,
        ContractChangeBasis basis,
        Action<string, string> error,
        Action<string, string> warning)
    {
        if (!basis.AllowsPriceChange && input.NewPrice is not null &&
            input.NewPrice.Value != input.ContractPrice)
        {
            error("newPrice", "Выбранное основание не допускает изменения цены контракта.");
        }

        if (input.NewPrice is not null)
        {
            if (input.NewPrice <= 0)
            {
                error("newPrice", "Новая цена должна быть больше нуля.");
            }
            else if (basis.OnlyPriceReduction && input.NewPrice >= input.ContractPrice)
            {
                error("newPrice", "По этому основанию цена может только снижаться.");
            }
            else if (input.NewPrice > input.ContractPrice)
            {
                var maxIncrease = basis.MaxPriceIncreasePercent;
                var limit = input.ContractPrice * (1 + maxIncrease / 100m);

                if (maxIncrease <= 0 || input.NewPrice > limit)
                {
                    error(
                        "newPrice",
                        maxIncrease <= 0
                            ? "Увеличение цены по выбранному основанию не допускается."
                            : $"Увеличение цены не может превышать {maxIncrease:0.##} % цены контракта.");
                }
            }
        }

        if (!basis.AllowsQuantityChange && input.NewQuantity is not null &&
            input.ContractQuantity is not null &&
            input.NewQuantity.Value != input.ContractQuantity.Value)
        {
            error("newQuantity", "Выбранное основание не допускает изменения количества или объёма.");
        }

        if (basis.AllowsQuantityChange)
        {
            if (input.NewQuantity is null or <= 0)
            {
                error("newQuantity", "Укажите новое количество или объём.");
            }
            else if (input.ContractQuantity is null or <= 0)
            {
                warning(
                    "contractQuantity",
                    "В карточке контракта не указано количество: проверить лимит 10 % автоматически нельзя.");
            }
            else
            {
                var oldQuantity = input.ContractQuantity.Value;
                var changePercent = Math.Abs(input.NewQuantity.Value - oldQuantity) / oldQuantity * 100m;

                if (changePercent > basis.MaxQuantityPercent)
                {
                    error(
                        "newQuantity",
                        $"Количество (объём) можно изменить не более чем на {basis.MaxQuantityPercent:0.##} %, а меняется на {changePercent:0.##} %.");
                }
            }
        }

        if (basis.RequiresGovernmentAct)
        {
            if (string.IsNullOrWhiteSpace(input.GovernmentActNumber) || input.GovernmentActDate is null)
            {
                error("governmentAct", "Укажите номер и дату акта органа власти — без него изменение недопустимо.");
            }
        }

        if (basis.RequiresFormula && string.IsNullOrWhiteSpace(input.FormulaText))
        {
            error("formulaText", "Для изменения цены по формуле укажите формулу и порядок расчёта.");
        }

        if (basis.RequiresUnitPrice)
        {
            if (input.UnitPrice is null or <= 0)
            {
                error("unitPrice", "Укажите новую цену единицы товара, работы или услуги.");
            }
            else if (input.InitialMaxPrice is > 0 && input.UnitPrice > input.InitialMaxPrice)
            {
                error("unitPrice", "Цена единицы не может превышать начальную максимальную цену контракта.");
            }
        }

        if (basis.RequiresNewSupplier && string.IsNullOrWhiteSpace(input.NewSupplier))
        {
            error("newSupplier", "Укажите нового поставщика (подрядчика, исполнителя).");
        }

        if (!string.IsNullOrWhiteSpace(input.NewSubject) && !basis.AllowsQualityImprovement)
        {
            error("newSubject", "Предмет контракта по этому основанию не меняется.");
        }

        if (basis.AllowsQualityImprovement && input.NewPrice > input.ContractPrice)
        {
            error("newPrice", "Улучшение характеристик не должно приводить к увеличению цены контракта.");
        }

        if (input.NewExecutionEndDate is not null &&
            input.NewExecutionEndDate != input.ContractExecutionEndDate &&
            !basis.RequiresGovernmentAct)
        {
            error(
                "newExecutionEndDate",
                "Срок исполнения по этому основанию не меняется: изменение срока допускается по акту органа власти.");
        }

        if (input.TerminationDate is not null)
        {
            warning("terminationDate", "Для изменения контракта дата расторжения не применяется.");
        }
    }

    private static void ValidateTermination(
        ContractChangeInput input,
        ContractChangeBasis basis,
        List<ContractChangeIssue> issues,
        Action<string, string> error,
        Action<string, string> warning)
    {
        if (input.TerminationDate is null)
        {
            error("terminationDate", "Укажите дату расторжения контракта.");
        }
        else if (input.TerminationDate < input.ChangeDate)
        {
            error("terminationDate", "Дата расторжения не может быть раньше даты документа.");
        }

        if (basis.RequiresCourtDecision)
        {
            if (string.IsNullOrWhiteSpace(input.CourtDecisionNumber) || input.CourtDecisionDate is null)
            {
                error("courtDecision", "Укажите номер и дату судебного акта.");
            }
            else if (input.TerminationDate is not null && input.TerminationDate < input.CourtDecisionDate)
            {
                error("terminationDate", "Дата расторжения не может быть раньше даты судебного акта.");
            }
        }

        if (basis.RequiresUnilateralNotice)
        {
            if (string.IsNullOrWhiteSpace(input.NoticeNumber) || input.NoticeDate is null)
            {
                error("notice", "Укажите номер и дату уведомления об одностороннем отказе.");
            }
            else
            {
                var earliest = input.NoticeDate.Value.AddDays(UnilateralNoticeDays);

                if (input.TerminationDate is not null && input.TerminationDate < earliest)
                {
                    error(
                        "terminationDate",
                        $"Решение об одностороннем отказе вступает в силу не ранее чем через {UnilateralNoticeDays} дней с даты уведомления ({earliest:dd.MM.yyyy}).");
                }
            }
        }

        if (input.NewPrice is not null && input.NewPrice != input.ContractPrice)
        {
            warning("newPrice", "При расторжении цена контракта не меняется — значение сохранено только для расчётов.");
        }

        if (input.NewQuantity is not null || !string.IsNullOrWhiteSpace(input.NewSupplier) ||
            !string.IsNullOrWhiteSpace(input.NewSubject))
        {
            warning("conditions", "Условия исполнения при расторжении не меняются: заполненные новые значения не применяются.");
        }
    }

    private static void ValidateDocuments(
        ContractChangeInput input,
        ContractChangeBasis basis,
        IReadOnlyList<ContractChangeDocument> documents,
        ContractChangeStatus status,
        Action<string, string> error)
    {
        if (status is ContractChangeStatus.Draft or ContractChangeStatus.Cancelled)
        {
            return;
        }

        var requiredType = RequiredDocumentType(input, basis);
        var hasRequired = documents.Any(document => document.Type == requiredType) ||
                          (requiredType == ContractChangeDocumentType.Notice &&
                           documents.Any(document => document.Type == ContractChangeDocumentType.Decision));

        if (!hasRequired)
        {
            error(
                "documents",
                $"Для этого статуса нужен документ «{requiredType.Ru()}»: без него согласование и подписание невозможны.");
        }

        if (documents.Count == 0)
        {
            error("documents", "Не сформирован ни один документ по изменению или расторжению.");
        }
    }

    private static ContractChangeDocumentType RequiredDocumentType(
        ContractChangeInput input,
        ContractChangeBasis basis)
    {
        if (input.Kind == ContractChangeKind.Modification)
        {
            return ContractChangeDocumentType.Agreement;
        }

        if (basis.RequiresCourtDecision)
        {
            return ContractChangeDocumentType.CourtDecision;
        }

        return basis.RequiresUnilateralNotice
            ? ContractChangeDocumentType.Notice
            : ContractChangeDocumentType.Agreement;
    }

    private static void ValidateEis(
        ContractChangeInput input,
        ContractChangeBasis basis,
        ContractChangeStatus status,
        Action<string, string> error)
    {
        if (status != ContractChangeStatus.RegisteredInEis || !basis.RequiresEisRegistration)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input.EisRegistryNumber))
        {
            error("eisRegistryNumber", "Для регистрации в ЕИС нужен реестровый номер контракта.");
        }
    }
}
