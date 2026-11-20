namespace Csms.Domain;

/// <summary>
/// Документ об изменении или расторжении контракта.
///
/// Сущность ведёт себя как небольшая машина состояний: статус меняется только
/// методами, каждый метод заново прогоняет правила ст. 95 и пишет событие
/// аудита. Репозиторий не может «назначить» статус напрямую — восстановление
/// из хранилища возвращает уже случившееся состояние.
/// </summary>
public sealed class ContractChange
{
    private readonly List<ContractChangeDocument> _documents = [];
    private readonly List<ContractChangeEvent> _events = [];

    private ContractChange(
        Guid id,
        ContractChangeInput input,
        ContractChangeStatus status,
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

    public ContractChangeInput Input { get; private set; }

    public ContractChangeStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Ревизия записи — токен оптимистичной блокировки. Домен её не меняет:
    /// значение приходит из хранилища при чтении и увеличивается там же после
    /// успешной записи. Второй редактор присылает устаревшую ревизию и получает
    /// конфликт вместо «тихой» перезаписи чужой правки.
    /// </summary>
    public int Revision { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }

    public DateTimeOffset? RegisteredAt { get; private set; }

    public string? SignedBy { get; private set; }

    public string? RegisteredBy { get; private set; }

    public string? EisRegistrationNumber { get; private set; }

    public DateOnly? EisRegistrationDate { get; private set; }

    public string? RejectedReason { get; private set; }

    public string? CancelledReason { get; private set; }

    public IReadOnlyList<ContractChangeDocument> Documents => _documents;

    public IReadOnlyList<ContractChangeEvent> Events => _events;

    /// <summary>Текущие замечания по правилам — без исключения, для интерфейса и API.</summary>
    public IReadOnlyList<ContractChangeIssue> Validate() =>
        ContractChangeRules.Validate(Input, _documents, Status);

    public static ContractChange Create(ContractChangeInput input, DateTimeOffset now)
    {
        var change = new ContractChange(Guid.NewGuid(), input, ContractChangeStatus.Draft, now, now)
        {
            Revision = 1,
        };

        change.EnsureValid("создать документ");
        change._events.Add(new ContractChangeEvent(
            now,
            ContractChangeStatus.Draft,
            ContractChangeStatus.Draft,
            "Создан",
            $"Создан документ: {input.Kind.Ru()}, основание «{ContractChangeBasisCatalog.Find(input.BasisCode)?.Title}»",
            NormalizeActor(input.CreatedBy)));

        return change;
    }

    /// <summary>
    /// Изменить черновик. Отклонённый документ тоже можно вернуть в работу:
    /// это новая редакция, а не «тихое» исправление согласованного текста.
    /// </summary>
    public void UpdateDraft(ContractChangeInput input, string actor, DateTimeOffset now)
    {
        EnsureStatus("изменить документ", ContractChangeStatus.Draft, ContractChangeStatus.Rejected);

        Input = input;
        RejectedReason = null;
        Touch(actor, now, "Черновик обновлён", "Изменены условия или реквизиты документа");
    }

    public ContractChangeDocument AddDocument(
        ContractChangeDocumentType type,
        string title,
        string content,
        string actor,
        DateTimeOffset now)
    {
        EnsureStatus(
            "добавить документ",
            ContractChangeStatus.Draft,
            ContractChangeStatus.Rejected,
            ContractChangeStatus.OnApproval);

        var document = ContractChangeDocument.Create(type, title, content, actor, now);
        _documents.Add(document);
        Touch(actor, now, "Добавлен документ", $"{type.Ru()} «{document.Title}», редакция {document.Version}");

        return document;
    }

    public void RemoveDocument(Guid documentId, string actor, DateTimeOffset now)
    {
        EnsureStatus("удалить документ", ContractChangeStatus.Draft, ContractChangeStatus.Rejected);

        var document = _documents.FirstOrDefault(item => item.Id == documentId)
                       ?? throw new DomainException("Документ не найден.");

        _documents.Remove(document);
        Touch(actor, now, "Удалён документ", $"{document.Type.Ru()} «{document.Title}»");
    }

    /// <summary>Отправить на согласование: с этого момента состав документов фиксируется.</summary>
    public void Submit(string actor, DateTimeOffset now)
    {
        EnsureStatus("отправить на согласование", ContractChangeStatus.Draft, ContractChangeStatus.Rejected);

        // Проверяем по целевому статусу: в черновике комплектность документов
        // ещё не требуется, а на согласовании — уже да. Иначе документ без
        // соглашения уходил бы в работу и «застревал» на шаге согласования.
        EnsureValid("отправить на согласование", ContractChangeStatus.OnApproval);

        SubmittedAt = now;
        RejectedReason = null;
        Transition(ContractChangeStatus.OnApproval, actor, now, "Отправлен на согласование",
            "Документ проверен правилами ст. 95 44-ФЗ");
    }

    public void Approve(string actor, DateTimeOffset now)
    {
        EnsureStatus("согласовать документ", ContractChangeStatus.OnApproval);
        EnsureValid("согласовать документ");

        ApprovedAt = now;
        Transition(ContractChangeStatus.Approved, actor, now, "Согласован",
            "Документ согласован уполномоченным лицом");
    }

    public void Reject(string actor, string reason, DateTimeOffset now)
    {
        EnsureStatus("отклонить документ", ContractChangeStatus.OnApproval);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Укажите причину отклонения: без неё автор не поймёт, что исправлять.");
        }

        RejectedReason = reason.Trim();
        Transition(ContractChangeStatus.Rejected, actor, now, "Отклонён", RejectedReason);
    }

    public void Sign(string actor, DateTimeOffset now)
    {
        EnsureStatus("подписать документ", ContractChangeStatus.Approved);

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainException("Не указан подписант документа.");
        }

        EnsureValid("подписать документ");

        if (string.Equals(actor.Trim(), Input.CreatedBy.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // Это не запрет: у малого заказчика одно лицо может готовить и
            // подписывать. Но факт совпадения должен остаться в журнале.
            _events.Add(new ContractChangeEvent(
                now,
                Status,
                Status,
                "Предупреждение",
                "Подписант совпадает с автором документа — проверьте разделение полномочий.",
                NormalizeActor(actor)));
        }

        SignedAt = now;
        SignedBy = actor.Trim();
        Transition(ContractChangeStatus.Signed, actor, now, "Подписан",
            "Документ подписан; дальнейшая правка текста запрещена");
    }

    /// <summary>Зафиксировать регистрацию в ЕИС (номер и дату присваивает портал).</summary>
    public void RegisterInEis(
        string actor,
        string registrationNumber,
        DateOnly registrationDate,
        DateTimeOffset now)
    {
        EnsureStatus("зарегистрировать в ЕИС", ContractChangeStatus.Signed);

        if (string.IsNullOrWhiteSpace(registrationNumber))
        {
            throw new DomainException("Укажите номер, под которым документ зарегистрирован в ЕИС.");
        }

        if (registrationDate < Input.ChangeDate)
        {
            throw new DomainException("Дата регистрации в ЕИС не может быть раньше даты документа.");
        }

        EisRegistrationNumber = registrationNumber.Trim();
        EisRegistrationDate = registrationDate;
        RegisteredAt = now;
        RegisteredBy = actor.Trim();

        // Регистрация — это и есть переход в статус «Зарегистрирован в ЕИС»:
        // проверяем его требования (реестровый номер контракта, документы),
        // а не требования предыдущего шага.
        EnsureValid("зарегистрировать в ЕИС", ContractChangeStatus.RegisteredInEis);
        Transition(ContractChangeStatus.RegisteredInEis, actor, now, "Зарегистрирован в ЕИС",
            $"Регистрационный номер {EisRegistrationNumber} от {registrationDate:dd.MM.yyyy}");
    }

    public void Cancel(string actor, string reason, DateTimeOffset now)
    {
        if (Status == ContractChangeStatus.RegisteredInEis)
        {
            throw new DomainException("Зарегистрированный в ЕИС документ отменить нельзя: требуется отдельная процедура.");
        }

        if (Status == ContractChangeStatus.Cancelled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Укажите причину отмены документа.");
        }

        CancelledReason = reason.Trim();
        Transition(ContractChangeStatus.Cancelled, actor, now, "Отменён", CancelledReason);
    }

    /// <summary>Восстановление из хранилища: только для репозитория.</summary>
    public static ContractChange Restore(
        Guid id,
        ContractChangeInput input,
        ContractChangeStatus status,
        IReadOnlyList<ContractChangeDocument> documents,
        IReadOnlyList<ContractChangeEvent> events,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? submittedAt,
        DateTimeOffset? approvedAt,
        DateTimeOffset? signedAt,
        string? signedBy,
        DateTimeOffset? registeredAt,
        string? registeredBy,
        string? eisRegistrationNumber,
        DateOnly? eisRegistrationDate,
        string? rejectedReason,
        string? cancelledReason,
        int revision)
    {
        var change = new ContractChange(id, input, status, createdAt, updatedAt)
        {
            Revision = revision,
            SubmittedAt = submittedAt,
            ApprovedAt = approvedAt,
            SignedAt = signedAt,
            SignedBy = signedBy,
            RegisteredAt = registeredAt,
            RegisteredBy = registeredBy,
            EisRegistrationNumber = eisRegistrationNumber,
            EisRegistrationDate = eisRegistrationDate,
            RejectedReason = rejectedReason,
            CancelledReason = cancelledReason,
        };

        change._documents.AddRange(documents);
        change._events.AddRange(events);

        return change;
    }

    /// <summary>
    /// Принять ревизию, записанную хранилищем. Вызывается только после успешной
    /// записи: до этого документ в памяти считает актуальной прежнюю ревизию.
    /// </summary>
    public void AcceptRevision(int revision) => Revision = revision;

    /// <summary>
    /// Проверить правила и превратить ошибки в понятное исключение.
    /// </summary>
    /// <param name="action">Что пытается сделать пользователь — попадает в текст ошибки.</param>
    /// <param name="asStatus">
    /// Статус, в котором нужно проверить документ. По умолчанию текущий; переходы
    /// передают целевой, чтобы правила комплектности применялись к результату.
    /// </param>
    private void EnsureValid(string action, ContractChangeStatus? asStatus = null)
    {
        var errors = ContractChangeRules
            .Validate(Input, _documents, asStatus ?? Status)
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

    private void EnsureStatus(string action, params ContractChangeStatus[] allowed)
    {
        if (allowed.Contains(Status))
        {
            return;
        }

        var expected = string.Join(" или ", allowed.Select(status => $"«{status.Ru()}»"));

        throw new DomainException(
            $"Нельзя {action}: документ в статусе «{Status.Ru()}», а требуется {expected}.");
    }

    private void Transition(
        ContractChangeStatus to,
        string actor,
        DateTimeOffset now,
        string action,
        string comment)
    {
        var from = Status;

        Status = to;
        UpdatedAt = now;
        _events.Add(new ContractChangeEvent(now, from, to, action, comment, NormalizeActor(actor)));
    }

    private void Touch(string actor, DateTimeOffset now, string action, string comment)
    {
        UpdatedAt = now;
        _events.Add(new ContractChangeEvent(now, Status, Status, action, comment, NormalizeActor(actor)));
    }

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "Система" : actor.Trim();
}
