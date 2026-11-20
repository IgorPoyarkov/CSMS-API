namespace Csms.Domain;

/// <summary>
/// Сведения о привлечении СМП/СОНКО по контракту.
///
/// Как и документ об изменении контракта, запись — небольшая машина состояний:
/// статус меняется только методами, каждый переход заново прогоняет правила и
/// пишет событие. Репозиторий не может «назначить» статус напрямую.
/// </summary>
public sealed class ContractSmpRecord
{
    private readonly List<ContractSmpDocument> _documents = [];
    private readonly List<ContractSmpEvent> _events = [];

    private ContractSmpRecord(
        Guid id,
        ContractSmpRecordInput input,
        ContractSmpStatus status,
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

    public ContractSmpRecordInput Input { get; private set; }

    public ContractSmpStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Токен оптимистичной блокировки; домен его меняет только после записи.</summary>
    public int Revision { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? AcceptedBy { get; private set; }
    public DateTimeOffset? RejectedAt { get; private set; }
    public string? RejectedReason { get; private set; }
    public DateTimeOffset? PushedAt { get; private set; }
    public string? PushedBy { get; private set; }
    public DateOnly? EisPublishDate { get; private set; }
    public string? EisRecordNumber { get; private set; }

    public IReadOnlyList<ContractSmpDocument> Documents => _documents;

    public IReadOnlyList<ContractSmpEvent> Events => _events;

    /// <summary>Удалять можно только неподтверждённые записи.</summary>
    public bool CanDelete => Status is ContractSmpStatus.Submitted or ContractSmpStatus.Rejected;

    /// <summary>Текущие замечания — для карточки и ответа API.</summary>
    public IReadOnlyList<ContractSmpIssue> Validate() =>
        ContractSmpRules.Validate(Input, _documents, Status, CreatedAt);

    public static ContractSmpRecord Create(ContractSmpRecordInput input, DateTimeOffset now)
    {
        var record = new ContractSmpRecord(Guid.NewGuid(), input, ContractSmpStatus.Submitted, now, now)
        {
            Revision = 1,
            SubmittedAt = now,
        };

        record.EnsureValid("подать сведения", ContractSmpStatus.Submitted);
        record._events.Add(new ContractSmpEvent(
            now,
            ContractSmpStatus.Submitted,
            ContractSmpStatus.Submitted,
            "Поданы",
            $"Сведения по договору № {input.DocumentNumber} от {input.DocumentDate:dd.MM.yyyy}",
            NormalizeActor(input.CreatedBy)));

        return record;
    }

    /// <summary>Правка доступна, пока запись не принята заказчиком.</summary>
    public void Update(ContractSmpRecordInput input, string actor, DateTimeOffset now)
    {
        EnsureStatus("изменить сведения", ContractSmpStatus.Submitted, ContractSmpStatus.Rejected);

        Input = input;
        RejectedAt = null;
        RejectedReason = null;
        Touch(actor, now, "Сведения обновлены", "Изменены реквизиты договора или документа");
    }

    public ContractSmpDocument AddDocument(
        ContractSmpDocumentType type,
        string title,
        string content,
        string actor,
        DateTimeOffset now)
    {
        EnsureStatus(
            "добавить документ",
            ContractSmpStatus.Submitted,
            ContractSmpStatus.Rejected,
            ContractSmpStatus.Accepted);

        var document = ContractSmpDocument.Create(type, title, content, actor, now);
        _documents.Add(document);
        Touch(actor, now, "Добавлен документ", $"{type.Ru()} «{document.Title}»");

        return document;
    }

    public void RemoveDocument(Guid documentId, string actor, DateTimeOffset now)
    {
        EnsureStatus("удалить документ", ContractSmpStatus.Submitted, ContractSmpStatus.Rejected);

        var document = _documents.FirstOrDefault(item => item.Id == documentId)
                       ?? throw new DomainException("Документ не найден.");

        _documents.Remove(document);
        Touch(actor, now, "Удалён документ", document.Title);
    }

    /// <summary>
    /// Принять сведения заказчиком. Оба подтверждения достоверности обязательны:
    /// заказчик не должен принимать неподтверждённый договор субподрядчика.
    /// </summary>
    public void Accept(string actor, DateTimeOffset now)
    {
        EnsureStatus("принять сведения", ContractSmpStatus.Submitted);
        EnsureValid("принять сведения", ContractSmpStatus.Accepted);

        AcceptedAt = now;
        AcceptedBy = NormalizeActor(actor);
        Transition(ContractSmpStatus.Accepted, actor, now, "Приняты заказчиком",
            "Подтверждения подрядчика и заказчика зафиксированы");
    }

    public void Reject(string actor, string reason, DateTimeOffset now)
    {
        EnsureStatus("отклонить сведения", ContractSmpStatus.Submitted);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Укажите причину отклонения: подрядчику нужно знать, что исправлять.");
        }

        RejectedAt = now;
        RejectedReason = reason.Trim();
        Transition(ContractSmpStatus.Rejected, actor, now, "Отклонены", RejectedReason);
    }

    /// <summary>Вернуть отклонённую запись в работу как новую редакцию.</summary>
    public void Resubmit(string actor, DateTimeOffset now)
    {
        EnsureStatus("повторно подать сведения", ContractSmpStatus.Rejected);

        RejectedAt = null;
        RejectedReason = null;
        SubmittedAt = now;
        Transition(ContractSmpStatus.Submitted, actor, now, "Повторно поданы",
            "Отклонённая запись возвращена в работу");
    }

    /// <summary>Зафиксировать выгрузку в карточку контракта ЕИС.</summary>
    public void PushToEis(
        string actor,
        DateOnly publishDate,
        string recordNumber,
        DateTimeOffset now)
    {
        EnsureStatus("выгрузить сведения в ЕИС", ContractSmpStatus.Accepted);

        if (string.IsNullOrWhiteSpace(recordNumber))
        {
            throw new DomainException("Укажите номер записи, присвоенный ЕИС.");
        }

        // Реквизиты выгрузки должны попасть и в снимок: правила проверяют
        // именно его, а не отдельные поля сущности.
        Input = Input with
        {
            EisPublishDate = publishDate,
            EisRecordNumber = recordNumber.Trim(),
        };
        EisPublishDate = publishDate;
        EisRecordNumber = recordNumber.Trim();
        PushedAt = now;
        PushedBy = NormalizeActor(actor);

        EnsureValid("выгрузить сведения в ЕИС", ContractSmpStatus.PushedToEis);
        Transition(ContractSmpStatus.PushedToEis, actor, now, "Выгружены в ЕИС",
            $"Запись ЕИС {EisRecordNumber} от {publishDate:dd.MM.yyyy}");
    }

    /// <summary>Восстановление из хранилища: только для репозитория.</summary>
    public static ContractSmpRecord Restore(
        Guid id,
        ContractSmpRecordInput input,
        ContractSmpStatus status,
        IReadOnlyList<ContractSmpDocument> documents,
        IReadOnlyList<ContractSmpEvent> events,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? submittedAt,
        DateTimeOffset? acceptedAt,
        string? acceptedBy,
        DateTimeOffset? rejectedAt,
        string? rejectedReason,
        DateTimeOffset? pushedAt,
        string? pushedBy,
        DateOnly? eisPublishDate,
        string? eisRecordNumber,
        int revision)
    {
        var record = new ContractSmpRecord(id, input, status, createdAt, updatedAt)
        {
            Revision = revision,
            SubmittedAt = submittedAt,
            AcceptedAt = acceptedAt,
            AcceptedBy = acceptedBy,
            RejectedAt = rejectedAt,
            RejectedReason = rejectedReason,
            PushedAt = pushedAt,
            PushedBy = pushedBy,
            EisPublishDate = eisPublishDate,
            EisRecordNumber = eisRecordNumber,
        };

        record._documents.AddRange(documents);
        record._events.AddRange(events);

        return record;
    }

    /// <summary>Принять ревизию, записанную хранилищем.</summary>
    public void AcceptRevision(int revision) => Revision = revision;

    private void EnsureValid(string action, ContractSmpStatus? asStatus = null)
    {
        var errors = ContractSmpRules
            .Validate(Input, _documents, asStatus ?? Status, CreatedAt)
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

    private void EnsureStatus(string action, params ContractSmpStatus[] allowed)
    {
        if (allowed.Contains(Status))
        {
            return;
        }

        var expected = string.Join(" или ", allowed.Select(status => $"«{status.Ru()}»"));

        throw new DomainException(
            $"Нельзя {action}: запись в статусе «{Status.Ru()}», а требуется {expected}.");
    }

    private void Transition(
        ContractSmpStatus to,
        string actor,
        DateTimeOffset now,
        string action,
        string comment)
    {
        var from = Status;

        Status = to;
        UpdatedAt = now;
        _events.Add(new ContractSmpEvent(now, from, to, action, comment, NormalizeActor(actor)));
    }

    private void Touch(string actor, DateTimeOffset now, string action, string comment)
    {
        UpdatedAt = now;
        _events.Add(new ContractSmpEvent(now, Status, Status, action, comment, NormalizeActor(actor)));
    }

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "Система" : actor.Trim();
}
