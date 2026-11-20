namespace Csms.Domain;

/// <summary>
/// Документ о приёмке поставленного товара, выполненной работы или услуги
/// (электронное актирование, ст. 94 44-ФЗ).
///
/// Сущность ведёт себя как машина состояний: статус меняется только методами,
/// каждый метод заново прогоняет правила и пишет событие аудита. Итог приёмки
/// хранится отдельным полем с кодами ЕИС (<see cref="AcceptanceResult"/>),
/// чтобы документ можно было передать в ЕИС без пересчёта смысла статуса.
/// </summary>
public sealed class ContractAcceptance
{
    private readonly List<AcceptancePosition> _positions = [];
    private readonly List<AcceptanceDefect> _defects = [];
    private readonly List<AcceptanceEvent> _events = [];

    private ContractAcceptance(
        Guid id,
        AcceptanceInput input,
        AcceptanceStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Input = input;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public AcceptanceInput Input { get; private set; }

    public AcceptanceStatus Status { get; private set; }

    /// <summary>Итог приёмки по кодам ЕИС; null, пока заказчик не рассмотрел документ.</summary>
    public AcceptanceResult? Result { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Ревизия записи — токен оптимистичной блокировки. Домен её не меняет:
    /// значение приходит из хранилища и увеличивается там после записи.
    /// </summary>
    public int Revision { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }

    public string? SignedBy { get; private set; }

    public DateTimeOffset? RegisteredAt { get; private set; }

    public string? RegisteredBy { get; private set; }

    public string? EisRegistrationNumber { get; private set; }

    public DateOnly? EisRegistrationDate { get; private set; }

    public string? RefusalReason { get; private set; }

    public string? ReturnComment { get; private set; }

    public string? CancelledReason { get; private set; }

    public IReadOnlyList<AcceptancePosition> Positions => _positions;

    public IReadOnlyList<AcceptanceDefect> Defects => _defects;

    public IReadOnlyList<AcceptanceEvent> Events => _events;

    public decimal TotalWithVat => _positions.Sum(position => position.TotalWithVat);

    public decimal VatAmount => _positions.Sum(position => position.VatAmount);

    public decimal TotalWithoutVat => _positions.Sum(position => position.TotalWithoutVat);

    /// <summary>
    /// Предельный срок рассмотрения документа заказчиком (ч. 13 ст. 94):
    /// дата документа плюс срок приёмки из контракта, в рабочих днях.
    /// </summary>
    public DateOnly? ReviewDeadline =>
        Input.DocumentDate is null
            ? null
            : AcceptanceCalendar.AddWorkingDays(Input.DocumentDate.Value, Input.AcceptanceTermDays);

    /// <summary>Текущие замечания по правилам — без исключения, для интерфейса и API.</summary>
    public IReadOnlyList<AcceptanceIssue> Validate() =>
        AcceptanceRules.Validate(Input, _positions, _defects, Status);

    public static ContractAcceptance Create(AcceptanceInput input, DateTimeOffset now)
    {
        var acceptance = new ContractAcceptance(Guid.NewGuid(), input, AcceptanceStatus.Draft, now, now)
        {
            Revision = 1,
        };

        acceptance.EnsureValid("создать документ о приёмке");

        acceptance._events.Add(new AcceptanceEvent(
            now,
            AcceptanceStatus.Draft,
            AcceptanceStatus.Draft,
            "Создан",
            $"Создан документ: {input.Kind.Ru()}, контракт № {input.ContractNumber}",
            NormalizeActor(input.CreatedBy)));

        return acceptance;
    }

    /// <summary>
    /// Изменить черновик. Возвращённый для уточнения документ тоже можно
    /// исправить: это новая редакция, а не «тихое» исправление отправленного.
    /// </summary>
    public void UpdateDraft(AcceptanceInput input, string actor, DateTimeOffset now)
    {
        EnsureStatus(
            "изменить документ",
            AcceptanceStatus.Draft,
            AcceptanceStatus.ReturnedForRevision);

        Input = input;
        Touch(actor, now, "Черновик обновлён", "Изменены реквизиты или условия приёмки");
    }

    public AcceptancePosition AddPosition(
        string name,
        string? okpd2,
        string unit,
        decimal quantity,
        decimal unitPrice,
        decimal vatRate,
        string? countryOrigin,
        string? registryNumber,
        string actor,
        DateTimeOffset now)
    {
        EnsurePositionsEditable("добавить позицию");

        var position = AcceptancePosition.Create(
            name, okpd2, unit, quantity, unitPrice, vatRate, countryOrigin, registryNumber);

        _positions.Add(position);
        Touch(
            actor,
            now,
            "Добавлена позиция",
            $"{position.Name}: {position.Quantity:0.###} {position.Unit} × {position.UnitPrice:0.00} ₽");

        return position;
    }

    public void RemovePosition(Guid positionId, string actor, DateTimeOffset now)
    {
        EnsurePositionsEditable("удалить позицию");

        var position = _positions.FirstOrDefault(item => item.Id == positionId)
                       ?? throw new DomainException("Позиция не найдена в документе.");

        // Недостатки ссылаются на позицию: без неё требование «заменить»
        // повисает в воздухе.
        if (_defects.Any(defect => defect.PositionId == positionId))
        {
            throw new DomainException(
                "По позиции уже зафиксированы недостатки: сначала удалите их, затем позицию.");
        }

        _positions.Remove(position);
        Touch(actor, now, "Удалена позиция", position.Name);
    }

    public AcceptanceDefect AddDefect(
        Guid? positionId,
        string description,
        decimal quantity,
        AcceptanceDefectRequirement requirement,
        string actor,
        DateTimeOffset now)
    {
        EnsureStatus(
            "добавить недостаток",
            AcceptanceStatus.Draft,
            AcceptanceStatus.Submitted,
            AcceptanceStatus.ReturnedForRevision);

        if (positionId is not null && _positions.All(position => position.Id != positionId))
        {
            throw new DomainException("Недостаток ссылается на позицию, которой нет в документе.");
        }

        var defect = AcceptanceDefect.Create(positionId, description, quantity, requirement);
        _defects.Add(defect);
        Touch(
            actor,
            now,
            "Зафиксирован недостаток",
            $"{defect.Description} ({defect.Requirement.Ru()})");

        return defect;
    }

    public void RemoveDefect(Guid defectId, string actor, DateTimeOffset now)
    {
        EnsureStatus(
            "удалить недостаток",
            AcceptanceStatus.Draft,
            AcceptanceStatus.Submitted,
            AcceptanceStatus.ReturnedForRevision);

        var defect = _defects.FirstOrDefault(item => item.Id == defectId)
                     ?? throw new DomainException("Недостаток не найден в документе.");

        _defects.Remove(defect);
        Touch(actor, now, "Недостаток снят", defect.Description);
    }

    /// <summary>Направить документ заказчику: с этого момента состав позиций фиксируется.</summary>
    public void Submit(string actor, DateTimeOffset now)
    {
        EnsureStatus(
            "направить документ заказчику",
            AcceptanceStatus.Draft,
            AcceptanceStatus.ReturnedForRevision);

        EnsureValid("направить документ заказчику", AcceptanceStatus.Submitted);

        SubmittedAt = now;
        ReturnComment = null;
        Transition(AcceptanceStatus.Submitted, actor, now, "Направлен заказчику",
            $"Документ проверен правилами ст. 94 44-ФЗ; срок рассмотрения — до {FormatDeadline()}");
    }

    /// <summary>
    /// Итог приёмки. Приёмка с расхождениями и без них — разные коды ЕИС,
    /// поэтому результат передаётся явно, а не выводится из наличия претензий.
    /// </summary>
    public void Accept(AcceptanceResult result, string actor, string? comment, DateTimeOffset now)
    {
        EnsureStatus("подписать итог приёмки", AcceptanceStatus.Submitted);

        if (result is not (AcceptanceResult.Accepted or AcceptanceResult.AcceptedWithDiscrepancies))
        {
            throw new DomainException(
                "Для приёмки допустимы итоги «принято без расхождений» или «принято с расхождениями».");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainException("Не указано лицо, подписавшее документ со стороны заказчика.");
        }

        var target = result == AcceptanceResult.Accepted
            ? AcceptanceStatus.Accepted
            : AcceptanceStatus.AcceptedWithDiscrepancies;

        EnsureValid("подписать итог приёмки", target);

        Result = result;
        ReviewedAt = now;
        SignedAt = now;
        SignedBy = actor.Trim();

        Transition(
            target,
            actor,
            now,
            result.Ru(),
            string.IsNullOrWhiteSpace(comment) ? "Документ подписан заказчиком" : comment.Trim());
    }

    /// <summary>Мотивированный отказ: без причины и перечня недостатков он не мотивирован.</summary>
    public void Refuse(string actor, string reason, DateTimeOffset now)
    {
        EnsureStatus("оформить мотивированный отказ", AcceptanceStatus.Submitted);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            throw new DomainException(
                "Опишите причину отказа (не менее 10 символов): отказ должен быть мотивированным.");
        }

        EnsureValid("оформить мотивированный отказ", AcceptanceStatus.Refused);

        Result = AcceptanceResult.Refused;
        RefusalReason = reason.Trim();
        ReviewedAt = now;

        Transition(AcceptanceStatus.Refused, actor, now, "Мотивированный отказ", RefusalReason);
    }

    /// <summary>Вернуть документ на уточнение (код ЕИС 0) без отказа от приёмки.</summary>
    public void ReturnForRevision(string actor, string comment, DateTimeOffset now)
    {
        EnsureStatus("вернуть документ на уточнение", AcceptanceStatus.Submitted);

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new DomainException("Укажите, что именно нужно уточнить в документе.");
        }

        Result = AcceptanceResult.ReturnedForRevision;
        ReturnComment = comment.Trim();
        ReviewedAt = now;

        Transition(
            AcceptanceStatus.ReturnedForRevision,
            actor,
            now,
            "Возвращено для уточнения",
            ReturnComment);
    }

    /// <summary>Зафиксировать регистрацию документа в ЕИС (номер и дату присваивает портал).</summary>
    public void RegisterInEis(
        string actor,
        string registrationNumber,
        DateOnly registrationDate,
        DateTimeOffset now)
    {
        EnsureStatus(
            "зарегистрировать документ в ЕИС",
            AcceptanceStatus.Accepted,
            AcceptanceStatus.AcceptedWithDiscrepancies);

        if (string.IsNullOrWhiteSpace(registrationNumber))
        {
            throw new DomainException("Укажите номер, под которым документ зарегистрирован в ЕИС.");
        }

        if (Input.DocumentDate is not null && registrationDate < Input.DocumentDate.Value)
        {
            throw new DomainException("Дата регистрации в ЕИС не может быть раньше даты документа.");
        }

        EisRegistrationNumber = registrationNumber.Trim();
        EisRegistrationDate = registrationDate;
        RegisteredAt = now;
        RegisteredBy = actor.Trim();

        EnsureValid("зарегистрировать документ в ЕИС", AcceptanceStatus.RegisteredInEis);

        Transition(
            AcceptanceStatus.RegisteredInEis,
            actor,
            now,
            "Зарегистрирован в ЕИС",
            $"Регистрационный номер {EisRegistrationNumber} от {registrationDate:dd.MM.yyyy}");
    }

    public void Cancel(string actor, string reason, DateTimeOffset now)
    {
        if (Status == AcceptanceStatus.RegisteredInEis)
        {
            throw new DomainException(
                "Зарегистрированный в ЕИС документ отменить нельзя: требуется отдельная процедура.");
        }

        if (Status == AcceptanceStatus.Cancelled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Укажите причину отмены документа.");
        }

        CancelledReason = reason.Trim();
        Transition(AcceptanceStatus.Cancelled, actor, now, "Отменён", CancelledReason);
    }

    /// <summary>Восстановление из хранилища: только для репозитория.</summary>
    public static ContractAcceptance Restore(
        Guid id,
        AcceptanceInput input,
        AcceptanceStatus status,
        AcceptanceResult? result,
        IReadOnlyList<AcceptancePosition> positions,
        IReadOnlyList<AcceptanceDefect> defects,
        IReadOnlyList<AcceptanceEvent> events,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? submittedAt,
        DateTimeOffset? reviewedAt,
        DateTimeOffset? signedAt,
        string? signedBy,
        DateTimeOffset? registeredAt,
        string? registeredBy,
        string? eisRegistrationNumber,
        DateOnly? eisRegistrationDate,
        string? refusalReason,
        string? returnComment,
        string? cancelledReason,
        int revision)
    {
        var acceptance = new ContractAcceptance(id, input, status, createdAt, updatedAt)
        {
            Revision = revision,
            Result = result,
            SubmittedAt = submittedAt,
            ReviewedAt = reviewedAt,
            SignedAt = signedAt,
            SignedBy = signedBy,
            RegisteredAt = registeredAt,
            RegisteredBy = registeredBy,
            EisRegistrationNumber = eisRegistrationNumber,
            EisRegistrationDate = eisRegistrationDate,
            RefusalReason = refusalReason,
            ReturnComment = returnComment,
            CancelledReason = cancelledReason,
        };

        acceptance._positions.AddRange(positions);
        acceptance._defects.AddRange(defects);
        acceptance._events.AddRange(events);

        return acceptance;
    }

    /// <summary>
    /// Принять ревизию, записанную хранилищем. Вызывается только после успешной
    /// записи: до этого документ считает актуальной прежнюю ревизию.
    /// </summary>
    public void AcceptRevision(int revision) => Revision = revision;

    /// <summary>
    /// Проверить правила и превратить ошибки в понятное исключение.
    /// </summary>
    /// <param name="action">Что пытается сделать пользователь — попадает в текст ошибки.</param>
    /// <param name="asStatus">
    /// Статус, в котором нужно проверить документ. По умолчанию текущий; переходы
    /// передают целевой, чтобы правила применялись к результату, а не к исходному
    /// состоянию.
    /// </param>
    private void EnsureValid(string action, AcceptanceStatus? asStatus = null)
    {
        var errors = AcceptanceRules
            .Validate(Input, _positions, _defects, asStatus ?? Status)
            .Where(issue => issue.IsError)
            .ToList();

        if (errors.Count == 0)
        {
            return;
        }

        var summary = string.Join("; ", errors.Take(3).Select(issue => issue.Message));
        var rest = errors.Count > 3 ? $" … и ещё {errors.Count - 3}." : "";

        throw new DomainException($"Нельзя {action}: {summary}{rest}");
    }

    private void EnsureStatus(string action, params AcceptanceStatus[] allowed)
    {
        if (allowed.Contains(Status))
        {
            return;
        }

        var expected = string.Join(" или ", allowed.Select(status => $"«{status.Ru()}»"));

        throw new DomainException(
            $"Нельзя {action}: документ в статусе «{Status.Ru()}», а требуется {expected}.");
    }

    private void EnsurePositionsEditable(string action) =>
        EnsureStatus(action, AcceptanceStatus.Draft, AcceptanceStatus.ReturnedForRevision);

    private void Transition(
        AcceptanceStatus to,
        string actor,
        DateTimeOffset now,
        string action,
        string comment)
    {
        var from = Status;

        Status = to;
        UpdatedAt = now;
        _events.Add(new AcceptanceEvent(now, from, to, action, comment, NormalizeActor(actor)));
    }

    private void Touch(string actor, DateTimeOffset now, string action, string comment)
    {
        UpdatedAt = now;
        _events.Add(new AcceptanceEvent(now, Status, Status, action, comment, NormalizeActor(actor)));
    }

    private string FormatDeadline() =>
        ReviewDeadline is null ? "дата не определена" : ReviewDeadline.Value.ToString("dd.MM.yyyy");

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "Система" : actor.Trim();
}
