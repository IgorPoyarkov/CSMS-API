using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Машина состояний документа об изменении или расторжении: черновик →
/// согласование → согласован → подписан → ЕИС. Проверяется, что обойти шаг
/// нельзя, а каждое действие оставляет запись в журнале аудита.
/// </summary>
public class ContractChangeWorkflowTests
{
    private static ContractChange NewChange(ContractChangeKind kind = ContractChangeKind.Modification) =>
        ContractChange.Create(
            kind == ContractChangeKind.Modification
                ? ContractChangeSamples.Modification()
                : ContractChangeSamples.Termination(),
            ContractChangeSamples.Now);

    private static ContractChange NewChangeWithAgreement()
    {
        var change = NewChange();
        change.AddDocument(
            ContractChangeDocumentType.Agreement,
            string.Empty,
            "Текст соглашения об изменении цены контракта.",
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        return change;
    }

    private static ContractChange OnApproval()
    {
        var change = NewChangeWithAgreement();
        change.Submit(ContractChangeSamples.Actor, ContractChangeSamples.Now);

        return change;
    }

    private static ContractChange Approved()
    {
        var change = OnApproval();
        change.Approve(ContractChangeSamples.Approver, ContractChangeSamples.Now);

        return change;
    }

    private static ContractChange Signed()
    {
        var change = Approved();
        change.Sign(ContractChangeSamples.Approver, ContractChangeSamples.Now);

        return change;
    }

    [Fact]
    public void Create_StartsAsDraftAndWritesAuditEvent()
    {
        var change = NewChange();

        Assert.Equal(ContractChangeStatus.Draft, change.Status);
        Assert.Equal(ContractChangeSamples.Now, change.CreatedAt);
        Assert.Equal(ContractChangeSamples.Now, change.UpdatedAt);

        var entry = Assert.Single(change.Events);
        Assert.Equal("Создан", entry.Action);
        Assert.Equal(ContractChangeStatus.Draft, entry.From);
        Assert.Equal(ContractChangeStatus.Draft, entry.To);
        Assert.Equal(ContractChangeSamples.Actor, entry.Actor);
    }

    [Fact]
    public void Create_InvalidInput_Throws()
    {
        var error = Assert.Throws<DomainException>(() =>
            ContractChange.Create(
                ContractChangeSamples.Modification(contractPrice: 0),
                ContractChangeSamples.Now));

        Assert.Contains("Цена контракта", error.Message);
    }

    [Fact]
    public void Revision_IsStorageTokenAndWorkflowDoesNotChangeIt()
    {
        var change = NewChangeWithAgreement();
        change.Submit(ContractChangeSamples.Actor, ContractChangeSamples.Now);

        // Домен не трогает ревизию: иначе после каждой правки в памяти
        // получалось бы новое значение, и проверка в WHERE снова совпадала бы.
        Assert.Equal(1, change.Revision);

        change.AcceptRevision(7);

        Assert.Equal(7, change.Revision);
    }

    [Fact]
    public void Submit_WithoutAgreement_Throws()
    {
        var change = NewChange();

        var error = Assert.Throws<DomainException>(() =>
            change.Submit(ContractChangeSamples.Actor, ContractChangeSamples.Now));

        Assert.Contains("Нельзя отправить на согласование", error.Message);
        Assert.Equal(ContractChangeStatus.Draft, change.Status);
    }

    [Fact]
    public void Submit_WithAgreement_MovesToOnApproval()
    {
        var change = NewChangeWithAgreement();
        var later = ContractChangeSamples.Now.AddHours(1);

        change.Submit(ContractChangeSamples.Actor, later);

        Assert.Equal(ContractChangeStatus.OnApproval, change.Status);
        Assert.Equal(later, change.SubmittedAt);
        Assert.Equal(later, change.UpdatedAt);

        var entry = change.Events.Last();
        Assert.Equal(ContractChangeStatus.Draft, entry.From);
        Assert.Equal(ContractChangeStatus.OnApproval, entry.To);
        Assert.Equal("Отправлен на согласование", entry.Action);
    }

    [Fact]
    public void Approve_SkipsToApprovedAndKeepsTimestamp()
    {
        var change = OnApproval();
        var later = ContractChangeSamples.Now.AddHours(2);

        change.Approve(ContractChangeSamples.Approver, later);

        Assert.Equal(ContractChangeStatus.Approved, change.Status);
        Assert.Equal(later, change.ApprovedAt);
    }

    [Fact]
    public void Approve_FromDraft_Throws()
    {
        var change = NewChangeWithAgreement();

        var error = Assert.Throws<DomainException>(() =>
            change.Approve(ContractChangeSamples.Approver, ContractChangeSamples.Now));

        Assert.Contains("«Черновик»", error.Message);
        Assert.Contains("«На согласовании»", error.Message);
    }

    [Fact]
    public void Reject_RequiresReason()
    {
        var change = OnApproval();

        var error = Assert.Throws<DomainException>(() =>
            change.Reject(ContractChangeSamples.Approver, "   ", ContractChangeSamples.Now));

        Assert.Contains("Укажите причину отклонения", error.Message);
        Assert.Equal(ContractChangeStatus.OnApproval, change.Status);
    }

    [Fact]
    public void Reject_StoresReasonAndAllowsRework()
    {
        var change = OnApproval();
        change.Reject(
            ContractChangeSamples.Approver,
            "Не указан реестровый номер контракта.",
            ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.Rejected, change.Status);
        Assert.Equal("Не указан реестровый номер контракта.", change.RejectedReason);

        change.UpdateDraft(
            ContractChangeSamples.Modification(newPrice: 95_000m),
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.Rejected, change.Status);
        Assert.Null(change.RejectedReason);
        Assert.Contains(change.Events, entry => entry.Action == "Черновик обновлён");

        change.Submit(ContractChangeSamples.Actor, ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.OnApproval, change.Status);
        Assert.Null(change.RejectedReason);
    }

    [Fact]
    public void Sign_WithoutApproval_Throws()
    {
        var change = OnApproval();

        var error = Assert.Throws<DomainException>(() =>
            change.Sign(ContractChangeSamples.Approver, ContractChangeSamples.Now));

        Assert.Contains("Нельзя подписать документ", error.Message);
    }

    [Fact]
    public void Sign_WithoutActor_Throws()
    {
        var change = Approved();

        var error = Assert.Throws<DomainException>(() =>
            change.Sign("  ", ContractChangeSamples.Now));

        Assert.Contains("Не указан подписант", error.Message);
    }

    [Fact]
    public void Sign_RecordsSigner()
    {
        var change = Approved();
        var later = ContractChangeSamples.Now.AddDays(1);

        change.Sign(ContractChangeSamples.Approver, later);

        Assert.Equal(ContractChangeStatus.Signed, change.Status);
        Assert.Equal(ContractChangeSamples.Approver, change.SignedBy);
        Assert.Equal(later, change.SignedAt);
    }

    [Fact]
    public void Sign_ByAuthor_LeavesWarningInJournal()
    {
        // Совпадение автора и подписанта не запрещено, но должно быть видно
        // при проверке разделения полномочий.
        var change = Approved();

        change.Sign(ContractChangeSamples.Actor, ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.Signed, change.Status);
        Assert.Contains(change.Events, entry =>
            entry.Action == "Предупреждение" && entry.Comment.Contains("разделение полномочий"));
    }

    [Fact]
    public void Register_RequiresSignature()
    {
        var change = Approved();

        var error = Assert.Throws<DomainException>(() =>
            change.RegisterInEis(
                ContractChangeSamples.Approver,
                "Э-100",
                new DateOnly(2026, 11, 26),
                ContractChangeSamples.Now));

        Assert.Contains("Нельзя зарегистрировать в ЕИС", error.Message);
    }

    [Fact]
    public void Register_RejectsDateBeforeDocument()
    {
        var change = Signed();

        var error = Assert.Throws<DomainException>(() =>
            change.RegisterInEis(
                ContractChangeSamples.Approver,
                "Э-100",
                new DateOnly(2026, 10, 1),
                ContractChangeSamples.Now));

        Assert.Contains("не может быть раньше даты документа", error.Message);
    }

    [Fact]
    public void Register_RequiresNumber()
    {
        var change = Signed();

        var error = Assert.Throws<DomainException>(() =>
            change.RegisterInEis(
                ContractChangeSamples.Approver,
                " ",
                new DateOnly(2026, 11, 26),
                ContractChangeSamples.Now));

        Assert.Contains("Укажите номер", error.Message);
    }

    [Fact]
    public void Register_CompletesTheFlow()
    {
        var change = Signed();

        change.RegisterInEis(
            ContractChangeSamples.Approver,
            " Э-777 ",
            new DateOnly(2026, 11, 26),
            ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.RegisteredInEis, change.Status);
        Assert.Equal("Э-777", change.EisRegistrationNumber);
        Assert.Equal(new DateOnly(2026, 11, 26), change.EisRegistrationDate);
        Assert.Equal(ContractChangeSamples.Approver, change.RegisteredBy);
        Assert.Empty(change.Validate().Where(issue => issue.IsError));
    }

    [Fact]
    public void Events_DescribeEveryTransition()
    {
        var change = Signed();
        change.RegisterInEis(
            ContractChangeSamples.Approver,
            "Э-777",
            new DateOnly(2026, 11, 26),
            ContractChangeSamples.Now);

        var transitions = change.Events
            .Where(entry => entry.From != entry.To)
            .Select(entry => (entry.From, entry.To))
            .ToArray();

        Assert.Equal(
            [
                (ContractChangeStatus.Draft, ContractChangeStatus.OnApproval),
                (ContractChangeStatus.OnApproval, ContractChangeStatus.Approved),
                (ContractChangeStatus.Approved, ContractChangeStatus.Signed),
                (ContractChangeStatus.Signed, ContractChangeStatus.RegisteredInEis),
            ],
            transitions);
    }

    [Fact]
    public void Cancel_RequiresReason()
    {
        var change = NewChange();

        var error = Assert.Throws<DomainException>(() =>
            change.Cancel(ContractChangeSamples.Actor, "", ContractChangeSamples.Now));

        Assert.Contains("Укажите причину отмены", error.Message);
    }

    [Fact]
    public void Cancel_IsIdempotent()
    {
        var change = NewChange();
        change.Cancel(ContractChangeSamples.Actor, "Документ создан ошибочно.", ContractChangeSamples.Now);
        var eventsAfterFirstCancel = change.Events.Count;

        change.Cancel(ContractChangeSamples.Actor, "Повторная отмена.", ContractChangeSamples.Now);

        Assert.Equal(ContractChangeStatus.Cancelled, change.Status);
        Assert.Equal(eventsAfterFirstCancel, change.Events.Count);
        Assert.Equal("Документ создан ошибочно.", change.CancelledReason);
    }

    [Fact]
    public void Cancel_RegisteredDocument_Throws()
    {
        var change = Signed();
        change.RegisterInEis(
            ContractChangeSamples.Approver,
            "Э-777",
            new DateOnly(2026, 11, 26),
            ContractChangeSamples.Now);

        var error = Assert.Throws<DomainException>(() =>
            change.Cancel(ContractChangeSamples.Actor, "Передумали.", ContractChangeSamples.Now));

        Assert.Contains("требуется отдельная процедура", error.Message);
    }

    [Fact]
    public void AfterSubmit_ContentIsLocked()
    {
        var change = OnApproval();

        Assert.Throws<DomainException>(() =>
            change.UpdateDraft(
                ContractChangeSamples.Modification(newPrice: 80_000m),
                ContractChangeSamples.Actor,
                ContractChangeSamples.Now));

        Assert.Throws<DomainException>(() =>
            change.RemoveDocument(
                change.Documents[0].Id,
                ContractChangeSamples.Actor,
                ContractChangeSamples.Now));

        // Дополнить комплект на согласовании можно: например, приложением.
        change.AddDocument(
            ContractChangeDocumentType.Attachment,
            "Расчёт цены",
            "Расчёт новой цены контракта.",
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        Assert.Equal(2, change.Documents.Count);
        Assert.Equal(ContractChangeStatus.OnApproval, change.Status);
    }

    [Fact]
    public void RemoveDocument_UnknownId_Throws()
    {
        var change = NewChangeWithAgreement();

        var error = Assert.Throws<DomainException>(() =>
            change.RemoveDocument(Guid.NewGuid(), ContractChangeSamples.Actor, ContractChangeSamples.Now));

        Assert.Contains("Документ не найден", error.Message);
    }

    [Fact]
    public void Validate_ReportsProblemsWithoutThrowing()
    {
        var change = NewChangeWithAgreement();

        // Черновик можно привести в негодное состояние и увидеть замечания,
        // не получая исключение при каждом вводе.
        change.UpdateDraft(
            ContractChangeSamples.Modification(contractPrice: 0, reason: "коротко"),
            ContractChangeSamples.Actor,
            ContractChangeSamples.Now);

        var issues = change.Validate();

        Assert.Contains(issues, issue => issue.IsError && issue.Field == "contractPrice");
        Assert.Contains(issues, issue => issue.IsError && issue.Field == "reason");
    }
}
