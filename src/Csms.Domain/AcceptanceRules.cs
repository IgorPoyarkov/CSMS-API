namespace Csms.Domain;

/// <summary>Замечание проверки: блокирующая ошибка или предупреждение.</summary>
public sealed record AcceptanceIssue(string Severity, string Field, string Message)
{
    public bool IsError => Severity == "error";
}

/// <summary>
/// Условия приёмки и реквизиты документа.
///
/// Снимок контракта приходит с фронта из реестра контрактов: модуль не читает
/// json-server и не подменяет цену или статус контракта «своими». Условия
/// приёмки (срок, экспертиза, комиссия) — тоже снимок: правила проверяют их
/// так, как они были зафиксированы в контракте на момент приёмки.
/// </summary>
public sealed record AcceptanceInput(
    string OrganizationId,
    string ContractId,
    string ContractNumber,
    string ContractName,
    decimal ContractPrice,
    string ContractStatus,
    DateOnly? ContractDate,
    string? CustomerName,
    string? SupplierName,
    string? SupplierInn,
    string? Ikz,
    string? EisRegistryNumber,
    int? StageNumber,
    string? StageName,
    AcceptanceKind Kind,
    string? DocumentNumber,
    DateOnly? DocumentDate,
    string? CorrectedDocumentNumber,
    DateOnly? CorrectedDocumentDate,
    string? CorrectionReason,
    int AcceptanceTermDays,
    bool ExpertiseRequired,
    bool ExpertiseByThirdParty,
    string? ExpertiseOrganization,
    string? ExpertiseConclusionNumber,
    DateOnly? ExpertiseConclusionDate,
    bool CommissionRequired,
    string? CommissionDecisionNumber,
    DateOnly? CommissionDecisionDate,
    string? DeliveryPlace,
    string? Notes,
    string CreatedBy);

/// <summary>
/// Проверки документа о приёмке по ст. 94 44-ФЗ.
///
/// Это не «подсказки в форме»: те же правила вызываются при сохранении,
/// направлении заказчику, подписании итога и регистрации в ЕИС. Обойти их из
/// интерфейса нельзя.
/// </summary>
public static class AcceptanceRules
{
    /// <summary>Предельный срок рассмотрения документа заказчиком, ч. 13 ст. 94.</summary>
    public const int MaxReviewTermDays = 20;

    private static readonly string[] AcceptableContractStatuses =
        ["signed", "in_execution", "suspended"];

    public static IReadOnlyList<AcceptanceIssue> Validate(
        AcceptanceInput input,
        IReadOnlyList<AcceptancePosition> positions,
        IReadOnlyList<AcceptanceDefect> defects,
        AcceptanceStatus status)
    {
        var issues = new List<AcceptanceIssue>();

        void Error(string field, string message) =>
            issues.Add(new AcceptanceIssue("error", field, message));

        void Warning(string field, string message) =>
            issues.Add(new AcceptanceIssue("warning", field, message));

        var isDraft = status is AcceptanceStatus.Draft or AcceptanceStatus.Cancelled;

        // ─── Снимок контракта ───────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(input.OrganizationId))
        {
            Error("organizationId", "Не указана организация заказчика.");
        }

        if (string.IsNullOrWhiteSpace(input.ContractId))
        {
            Error("contractId", "Не указан контракт: приёмка должна быть привязана к реестровой записи.");
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

        if (!AcceptableContractStatuses.Contains(input.ContractStatus))
        {
            Error(
                "contractStatus",
                input.ContractStatus is "completed" or "terminated"
                    ? "Контракт уже исполнен или расторгнут: принять по нему больше нечего."
                    : "Приёмка возможна только по заключённому или исполняемому контракту.");
        }

        if (input.ContractDate is null)
        {
            Error("contractDate", "Не указана дата заключения контракта.");
        }

        if (input.AcceptanceTermDays < 1 || input.AcceptanceTermDays > MaxReviewTermDays)
        {
            Error(
                "acceptanceTermDays",
                $"Срок приёмки должен быть от 1 до {MaxReviewTermDays} рабочих дней (ч. 13 ст. 94 44-ФЗ).");
        }

        if (input.StageNumber is <= 0)
        {
            Error("stageNumber", "Номер этапа исполнения должен быть больше нуля.");
        }

        // ─── Реквизиты документа ────────────────────────────────────────────
        if (input.DocumentDate is null)
        {
            if (!isDraft)
            {
                Error("documentDate", "Не указана дата документа о приёмке.");
            }
        }
        else if (input.ContractDate is not null && input.DocumentDate < input.ContractDate)
        {
            Error("documentDate", "Дата документа не может быть раньше даты заключения контракта.");
        }

        if (string.IsNullOrWhiteSpace(input.DocumentNumber) && !isDraft)
        {
            Error("documentNumber", "Не указан номер документа о приёмке.");
        }

        ValidateKind(input, Error, Warning);
        ValidateExpertise(input, Error);
        ValidateCommission(input, Error, Warning);
        // В черновике перечень позиций ещё формируется: требовать его при
        // каждом сохранении — значит не дать сохранить начатый документ.
        ValidatePositions(input, positions, required: !isDraft, Error);
        ValidateDefects(positions, defects, status, Error);

        if (status == AcceptanceStatus.RegisteredInEis &&
            string.IsNullOrWhiteSpace(input.EisRegistryNumber))
        {
            Error("eisRegistryNumber", "Для регистрации в ЕИС нужен реестровый номер контракта.");
        }

        return issues;
    }

    /// <summary>Предельный срок рассмотрения документа заказчиком.</summary>
    public static DateOnly ReviewDeadline(DateOnly documentDate, int termDays) =>
        AcceptanceCalendar.AddWorkingDays(documentDate, termDays);

    private static void ValidateKind(
        AcceptanceInput input,
        Action<string, string> error,
        Action<string, string> warning)
    {
        if (input.Kind == AcceptanceKind.Correction)
        {
            if (string.IsNullOrWhiteSpace(input.CorrectedDocumentNumber) ||
                input.CorrectedDocumentDate is null)
            {
                error(
                    "correctedDocument",
                    "Укажите номер и дату исправляемого документа о приёмке.");
            }

            if (string.IsNullOrWhiteSpace(input.CorrectionReason) ||
                input.CorrectionReason.Trim().Length < 10)
            {
                error("correctionReason", "Опишите причину исправления (не менее 10 символов).");
            }

            if (input.CorrectedDocumentDate is not null &&
                input.DocumentDate is not null &&
                input.DocumentDate < input.CorrectedDocumentDate)
            {
                error(
                    "documentDate",
                    "Дата корректировочного документа не может быть раньше исправляемого.");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(input.CorrectedDocumentNumber) ||
            input.CorrectedDocumentDate is not null)
        {
            error(
                "correctedDocument",
                "Для первичного документа не указываются реквизиты исправляемого документа.");
        }

        if (!string.IsNullOrWhiteSpace(input.CorrectionReason))
        {
            warning("correctionReason", "Причина исправления заполнена, но документ первичный.");
        }
    }

    private static void ValidateExpertise(AcceptanceInput input, Action<string, string> error)
    {
        // ч. 3 ст. 94: экспертиза проводится заказчиком своими силами или
        // с привлечением экспертов. Если контракт её не предусматривал,
        // привлекать стороннюю организацию нельзя.
        if (input.ExpertiseByThirdParty && !input.ExpertiseRequired)
        {
            error("expertise", "Экспертиза не предусмотрена условиями контракта.");
        }

        if (!input.ExpertiseByThirdParty)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input.ExpertiseOrganization))
        {
            error("expertiseOrganization", "Укажите организацию, проводившую экспертизу.");
        }

        if (string.IsNullOrWhiteSpace(input.ExpertiseConclusionNumber) ||
            input.ExpertiseConclusionDate is null)
        {
            error("expertiseConclusion", "Укажите номер и дату заключения экспертизы.");
        }
        else if (input.DocumentDate is not null &&
                 input.ExpertiseConclusionDate > input.DocumentDate)
        {
            error(
                "expertiseConclusion",
                "Заключение экспертизы не может быть позже даты документа о приёмке.");
        }
    }

    private static void ValidateCommission(
        AcceptanceInput input,
        Action<string, string> error,
        Action<string, string> warning)
    {
        if (!input.CommissionRequired)
        {
            if (!string.IsNullOrWhiteSpace(input.CommissionDecisionNumber) ||
                input.CommissionDecisionDate is not null)
            {
                warning(
                    "commission",
                    "Решение о приёмке комиссией указано, хотя комиссия контрактом не предусмотрена.");
            }

            return;
        }

        // ч. 6 ст. 94: для приёмки комиссией нужно решение заказчика о её создании.
        if (string.IsNullOrWhiteSpace(input.CommissionDecisionNumber) ||
            input.CommissionDecisionDate is null)
        {
            error("commission", "Укажите номер и дату решения о создании приёмочной комиссии.");
        }
        else if (input.DocumentDate is not null && input.CommissionDecisionDate > input.DocumentDate)
        {
            error("commission", "Решение о комиссии не может быть позже даты документа о приёмке.");
        }
    }

    private static void ValidatePositions(
        AcceptanceInput input,
        IReadOnlyList<AcceptancePosition> positions,
        bool required,
        Action<string, string> error)
    {
        if (positions.Count == 0)
        {
            if (required)
            {
                error("positions", "Не указана ни одна позиция: приёмка без перечня принятого невозможна.");
            }

            return;
        }

        foreach (var position in positions)
        {
            if (string.IsNullOrWhiteSpace(position.Name))
            {
                error("positions", "В позиции не указано наименование товара, работы или услуги.");
            }

            if (string.IsNullOrWhiteSpace(position.Unit))
            {
                error("positions", "В позиции не указана единица измерения.");
            }

            if (position.Quantity <= 0)
            {
                error("positions", $"Количество в позиции «{position.Name}» должно быть больше нуля.");
            }

            if (position.UnitPrice <= 0)
            {
                error("positions", $"Цена в позиции «{position.Name}» должна быть больше нуля.");
            }

            if (!AcceptanceVatRates.IsAllowed(position.VatRate))
            {
                error(
                    "positions",
                    $"Ставка НДС {position.VatRate:0.##} % в позиции «{position.Name}» не применяется.");
            }
        }

        var total = positions.Sum(position => position.TotalWithVat);

        if (total <= 0)
        {
            error("positions", "Стоимость принятого должна быть больше нуля.");
            return;
        }

        if (input.ContractPrice <= 0 || total <= input.ContractPrice)
        {
            return;
        }

        if (input.Kind == AcceptanceKind.Correction)
        {
            // Корректировочный документ может перераспределять суммы между
            // позициями, поэтому превышение цены контракта здесь не блокируем:
            // его проверяет заказчик при подписании.
            return;
        }

        error(
            "positions",
            $"Сумма документа {total:0.00} ₽ превышает цену контракта {input.ContractPrice:0.00} ₽.");
    }

    private static void ValidateDefects(
        IReadOnlyList<AcceptancePosition> positions,
        IReadOnlyList<AcceptanceDefect> defects,
        AcceptanceStatus status,
        Action<string, string> error)
    {
        if (status == AcceptanceStatus.AcceptedWithDiscrepancies && defects.Count == 0)
        {
            error(
                "defects",
                "Приёмка с расхождениями требует перечня недостатков: иначе это приёмка без претензий.");
        }

        if (status == AcceptanceStatus.Accepted && defects.Count > 0)
        {
            error(
                "defects",
                "Приёмка без расхождений не может содержать претензии: уберите недостатки или выберите «принято с расхождениями».");
        }

        if (status == AcceptanceStatus.Refused && defects.Count == 0)
        {
            error("defects", "Мотивированный отказ должен содержать перечень недостатков.");
        }

        foreach (var defect in defects)
        {
            if (string.IsNullOrWhiteSpace(defect.Description) || defect.Description.Trim().Length < 10)
            {
                error("defects", "Опишите недостаток (не менее 10 символов).");
            }

            if (defect.Quantity <= 0)
            {
                error("defects", "Количество по недостатку должно быть больше нуля.");
            }

            if (defect.PositionId is null)
            {
                continue;
            }

            var position = positions.FirstOrDefault(item => item.Id == defect.PositionId);

            if (position is null)
            {
                error("defects", "Недостаток ссылается на позицию, которой нет в документе.");
                continue;
            }

            if (defect.Quantity > position.Quantity)
            {
                error(
                    "defects",
                    $"По позиции «{position.Name}» недостаток заявлен на {defect.Quantity:0.###}, " +
                    $"а принято {position.Quantity:0.###}.");
            }
        }

        // Суммарно по позиции нельзя предъявить больше, чем в ней заявлено.
        foreach (var group in defects.Where(defect => defect.PositionId is not null)
                     .GroupBy(defect => defect.PositionId!.Value))
        {
            var position = positions.FirstOrDefault(item => item.Id == group.Key);

            if (position is null)
            {
                continue;
            }

            var quantity = group.Sum(defect => defect.Quantity);

            if (quantity > position.Quantity)
            {
                error(
                    "defects",
                    $"Суммарное количество недостатков по позиции «{position.Name}» " +
                    $"({quantity:0.###}) больше принятого ({position.Quantity:0.###}).");
            }
        }
    }
}

/// <summary>
/// Рабочие дни без учёта переносов праздников: для срока приёмки достаточно
/// исключить субботу и воскресенье, а праздничный календарь меняется каждый
/// год и не должен «зашиваться» в домен.
/// </summary>
public static class AcceptanceCalendar
{
    public static DateOnly AddWorkingDays(DateOnly date, int days)
    {
        if (days <= 0)
        {
            return date;
        }

        var result = date;
        var added = 0;

        while (added < days)
        {
            result = result.AddDays(1);

            if (result.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                added++;
            }
        }

        return result;
    }
}
