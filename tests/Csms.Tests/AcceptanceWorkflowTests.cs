using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Машина состояний документа о приёмке: черновик → направлен заказчику →
/// итог приёмки → ЕИС. Проверяется, что обойти шаг нельзя, а каждое действие
/// оставляет запись в журнале аудита.
/// </summary>
public class AcceptanceWorkflowTests
{
    private static AcceptanceResult Accepted => AcceptanceResult.Accepted;

    [Fact]
    public void Create_StartsAsDraftAndWritesAuditEvent()
    {
        var acceptance = AcceptanceSamples.Draft();

        Assert.Equal(AcceptanceStatus.Draft, acceptance.Status);
        Assert.Null(acceptance.Result);
        Assert.Equal(1, acceptance.Revision);
        Assert.Equal(AcceptanceSamples.Now, acceptance.CreatedAt);

        var entry = Assert.Single(acceptance.Events);
        Assert.Equal("Создан", entry.Action);
        Assert.Equal(AcceptanceStatus.Draft, entry.From);
        Assert.Equal(AcceptanceStatus.Draft, entry.To);
        Assert.Equal(AcceptanceSamples.Actor, entry.Actor);
    }

    [Fact]
    public void Create_InvalidInput_Throws()
    {
        var error = Assert.Throws<DomainException>(() =>
            ContractAcceptance.Create(
                AcceptanceSamples.Primary(contractPrice: 0),
                AcceptanceSamples.Now));

        Assert.Contains("Цена контракта", error.Message);
    }

    [Fact]
    public void AddPosition_ComputesDocumentTotals()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition(quantity: 3m, unitPrice: 1000m, vatRate: 20m);

        var position = Assert.Single(acceptance.Positions);
        Assert.Equal(3000m, position.TotalWithVat);
        Assert.Equal(500m, position.VatAmount);
        Assert.Equal(3000m, acceptance.TotalWithVat);
        Assert.Equal(500m, acceptance.VatAmount);
        Assert.Equal(2500m, acceptance.TotalWithoutVat);
    }

    [Fact]
    public void RemovePosition_UnknownId_Throws()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.RemovePosition(Guid.NewGuid(), AcceptanceSamples.Actor, AcceptanceSamples.Now));

        Assert.Contains("Позиция не найдена", error.Message);
    }

    [Fact]
    public void RemovePosition_WithDefects_Throws()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition();
        var position = acceptance.Positions[0];

        acceptance.AddDefect(
            position.Id,
            "Поставлено 5 шприцев с истёкшим сроком годности.",
            5m,
            AcceptanceDefectRequirement.Replace,
            AcceptanceSamples.Actor,
            AcceptanceSamples.Now);

        var error = Assert.Throws<DomainException>(() =>
            acceptance.RemovePosition(position.Id, AcceptanceSamples.Actor, AcceptanceSamples.Now));

        Assert.Contains("сначала удалите их", error.Message);
    }

    [Fact]
    public void Submit_WithoutPositions_Throws()
    {
        var acceptance = AcceptanceSamples.Draft();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Submit(AcceptanceSamples.Actor, AcceptanceSamples.Now));

        Assert.Contains("Нельзя направить документ заказчику", error.Message);
        Assert.Equal(AcceptanceStatus.Draft, acceptance.Status);
    }

    [Fact]
    public void Submit_SetsStatusAndReviewDeadline()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition();
        var later = AcceptanceSamples.Now.AddHours(1);

        acceptance.Submit(AcceptanceSamples.Actor, later);

        Assert.Equal(AcceptanceStatus.Submitted, acceptance.Status);
        Assert.Equal(later, acceptance.SubmittedAt);
        // 10 рабочих дней с 10.11.2026 — это 24.11.2026 (ч. 13 ст. 94).
        Assert.Equal(new DateOnly(2026, 11, 24), acceptance.ReviewDeadline);

        var entry = acceptance.Events.Last();
        Assert.Equal(AcceptanceStatus.Draft, entry.From);
        Assert.Equal(AcceptanceStatus.Submitted, entry.To);
        Assert.Equal("Направлен заказчику", entry.Action);
    }

    [Fact]
    public void AfterSubmit_PositionsAndDraftAreLocked()
    {
        var acceptance = AcceptanceSamples.Submitted();

        Assert.Throws<DomainException>(() =>
            acceptance.AddPosition(
                "Перчатки", null, "пара", 10m, 150m, 10m, null, null,
                AcceptanceSamples.Actor, AcceptanceSamples.Now));

        Assert.Throws<DomainException>(() =>
            acceptance.UpdateDraft(AcceptanceSamples.Primary(), AcceptanceSamples.Actor, AcceptanceSamples.Now));

        // Недостатки заказчик фиксирует именно на этом шаге.
        acceptance.AddDefect(
            acceptance.Positions[0].Id,
            "Поставлено 5 шприцев с истёкшим сроком годности.",
            5m,
            AcceptanceDefectRequirement.Replace,
            AcceptanceSamples.Customer,
            AcceptanceSamples.Now);

        Assert.Single(acceptance.Defects);
        Assert.Equal(AcceptanceStatus.Submitted, acceptance.Status);
    }

    [Fact]
    public void Accept_FromDraft_Throws()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Accept(Accepted, AcceptanceSamples.Customer, null, AcceptanceSamples.Now));

        Assert.Contains("«Черновик»", error.Message);
        Assert.Contains("«Направлен заказчику»", error.Message);
    }

    [Fact]
    public void Accept_WithoutActor_Throws()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Accept(Accepted, "  ", null, AcceptanceSamples.Now));

        Assert.Contains("Не указано лицо", error.Message);
    }

    [Fact]
    public void Accept_WithRefusedResult_Throws()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Accept(AcceptanceResult.Refused, AcceptanceSamples.Customer, null, AcceptanceSamples.Now));

        Assert.Contains("допустимы итоги", error.Message);
    }

    [Fact]
    public void Accept_SetsResultAndSigner()
    {
        var acceptance = AcceptanceSamples.Submitted();
        var later = AcceptanceSamples.Now.AddDays(1);

        acceptance.Accept(Accepted, AcceptanceSamples.Customer, "Замечаний нет", later);

        Assert.Equal(AcceptanceStatus.Accepted, acceptance.Status);
        Assert.Equal(Accepted, acceptance.Result);
        Assert.Equal(AcceptanceSamples.Customer, acceptance.SignedBy);
        Assert.Equal(later, acceptance.SignedAt);
        Assert.Equal(later, acceptance.ReviewedAt);
        Assert.DoesNotContain(acceptance.Validate(), issue => issue.IsError);
    }

    [Fact]
    public void Accept_WithDiscrepanciesWithoutDefects_Throws()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Accept(
                AcceptanceResult.AcceptedWithDiscrepancies,
                AcceptanceSamples.Customer,
                null,
                AcceptanceSamples.Now));

        Assert.Contains("требует перечня недостатков", error.Message);
    }

    [Fact]
    public void Accept_WithDiscrepanciesAndDefect_Succeeds()
    {
        var acceptance = AcceptanceSamples.Submitted();
        var position = acceptance.Positions[0];

        acceptance.AddDefect(
            position.Id,
            "Пять шприцев поставлены с истёкшим сроком годности.",
            5m,
            AcceptanceDefectRequirement.Replace,
            AcceptanceSamples.Customer,
            AcceptanceSamples.Now);

        acceptance.Accept(
            AcceptanceResult.AcceptedWithDiscrepancies,
            AcceptanceSamples.Customer,
            "Принять с заменой пяти шприцев",
            AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.AcceptedWithDiscrepancies, acceptance.Status);
        Assert.Equal(AcceptanceResult.AcceptedWithDiscrepancies, acceptance.Result);
    }

    [Fact]
    public void Refuse_RequiresReason()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Refuse(AcceptanceSamples.Customer, "нет", AcceptanceSamples.Now));

        Assert.Contains("мотивированным", error.Message);
        Assert.Equal(AcceptanceStatus.Submitted, acceptance.Status);
    }

    [Fact]
    public void Refuse_WithDefects_MovesToRefused()
    {
        var acceptance = AcceptanceSamples.Submitted();
        var position = acceptance.Positions[0];

        acceptance.AddDefect(
            position.Id,
            "Шприцы не соответствуют требованиям к стерильности.",
            100m,
            AcceptanceDefectRequirement.Reject,
            AcceptanceSamples.Customer,
            AcceptanceSamples.Now);

        acceptance.Refuse(
            AcceptanceSamples.Customer,
            "Товар не соответствует условиям контракта по качеству.",
            AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.Refused, acceptance.Status);
        Assert.Equal(AcceptanceResult.Refused, acceptance.Result);
        Assert.Contains("качеству", acceptance.RefusalReason);
    }

    [Fact]
    public void ReturnForRevision_RequiresComment()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.ReturnForRevision(AcceptanceSamples.Customer, " ", AcceptanceSamples.Now));

        Assert.Contains("что именно нужно уточнить", error.Message);
    }

    [Fact]
    public void Returned_CanBeReworkedAndResubmitted()
    {
        var acceptance = AcceptanceSamples.Submitted();
        acceptance.ReturnForRevision(
            AcceptanceSamples.Customer,
            "Не указана страна происхождения товара.",
            AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.ReturnedForRevision, acceptance.Status);
        Assert.Equal(AcceptanceResult.ReturnedForRevision, acceptance.Result);
        Assert.NotNull(acceptance.ReturnComment);

        acceptance.UpdateDraft(
            AcceptanceSamples.Primary(documentNumber: "АКТ-1"),
            AcceptanceSamples.Actor,
            AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.ReturnedForRevision, acceptance.Status);

        acceptance.Submit(AcceptanceSamples.Actor, AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.Submitted, acceptance.Status);
        Assert.Null(acceptance.ReturnComment);
    }

    [Fact]
    public void Register_RequiresAcceptedStatus()
    {
        var acceptance = AcceptanceSamples.Submitted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.RegisterInEis(
                AcceptanceSamples.Customer,
                "Э-100",
                new DateOnly(2026, 11, 25),
                AcceptanceSamples.Now));

        Assert.Contains("Нельзя зарегистрировать документ в ЕИС", error.Message);
    }

    [Fact]
    public void Register_RejectsDateBeforeDocument()
    {
        var acceptance = AcceptanceSamples.Accepted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.RegisterInEis(
                AcceptanceSamples.Customer,
                "Э-100",
                new DateOnly(2026, 10, 1),
                AcceptanceSamples.Now));

        Assert.Contains("не может быть раньше даты документа", error.Message);
    }

    [Fact]
    public void Register_RequiresNumber()
    {
        var acceptance = AcceptanceSamples.Accepted();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.RegisterInEis(
                AcceptanceSamples.Customer,
                " ",
                new DateOnly(2026, 11, 25),
                AcceptanceSamples.Now));

        Assert.Contains("Укажите номер", error.Message);
    }

    [Fact]
    public void Register_CompletesTheFlow()
    {
        var acceptance = AcceptanceSamples.Accepted();

        acceptance.RegisterInEis(
            AcceptanceSamples.Customer,
            " Э-777 ",
            new DateOnly(2026, 11, 25),
            AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.RegisteredInEis, acceptance.Status);
        Assert.Equal("Э-777", acceptance.EisRegistrationNumber);
        Assert.Equal(new DateOnly(2026, 11, 25), acceptance.EisRegistrationDate);
        Assert.Equal(AcceptanceSamples.Customer, acceptance.RegisteredBy);
        Assert.DoesNotContain(acceptance.Validate(), issue => issue.IsError);
    }

    [Fact]
    public void Events_DescribeEveryTransition()
    {
        var acceptance = AcceptanceSamples.Accepted();
        acceptance.RegisterInEis(
            AcceptanceSamples.Customer,
            "Э-777",
            new DateOnly(2026, 11, 25),
            AcceptanceSamples.Now);

        var transitions = acceptance.Events
            .Where(entry => entry.From != entry.To)
            .Select(entry => (entry.From, entry.To))
            .ToArray();

        Assert.Equal(
            [
                (AcceptanceStatus.Draft, AcceptanceStatus.Submitted),
                (AcceptanceStatus.Submitted, AcceptanceStatus.Accepted),
                (AcceptanceStatus.Accepted, AcceptanceStatus.RegisteredInEis),
            ],
            transitions);
    }

    [Fact]
    public void Cancel_RequiresReason()
    {
        var acceptance = AcceptanceSamples.Draft();

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Cancel(AcceptanceSamples.Actor, "", AcceptanceSamples.Now));

        Assert.Contains("Укажите причину отмены", error.Message);
    }

    [Fact]
    public void Cancel_IsIdempotent()
    {
        var acceptance = AcceptanceSamples.Draft();
        acceptance.Cancel(AcceptanceSamples.Actor, "Документ создан ошибочно.", AcceptanceSamples.Now);
        var eventsAfterFirstCancel = acceptance.Events.Count;

        acceptance.Cancel(AcceptanceSamples.Actor, "Повторная отмена.", AcceptanceSamples.Now);

        Assert.Equal(AcceptanceStatus.Cancelled, acceptance.Status);
        Assert.Equal(eventsAfterFirstCancel, acceptance.Events.Count);
        Assert.Equal("Документ создан ошибочно.", acceptance.CancelledReason);
    }

    [Fact]
    public void Cancel_RegisteredDocument_Throws()
    {
        var acceptance = AcceptanceSamples.Accepted();
        acceptance.RegisterInEis(
            AcceptanceSamples.Customer,
            "Э-777",
            new DateOnly(2026, 11, 25),
            AcceptanceSamples.Now);

        var error = Assert.Throws<DomainException>(() =>
            acceptance.Cancel(AcceptanceSamples.Actor, "Передумали.", AcceptanceSamples.Now));

        Assert.Contains("требуется отдельная процедура", error.Message);
    }

    [Fact]
    public void Validate_ReportsProblemsWithoutThrowing()
    {
        var acceptance = AcceptanceSamples.DraftWithPosition();

        // Черновик можно привести в негодное состояние и увидеть замечания,
        // не получая исключение при каждом вводе.
        acceptance.UpdateDraft(
            AcceptanceSamples.Primary(contractPrice: 0, acceptanceTermDays: 30),
            AcceptanceSamples.Actor,
            AcceptanceSamples.Now);

        var issues = acceptance.Validate();

        Assert.Contains(issues, issue => issue.IsError && issue.Field == "contractPrice");
        Assert.Contains(issues, issue => issue.IsError && issue.Field == "acceptanceTermDays");
    }

    [Fact]
    public void Revision_IsStorageTokenAndWorkflowDoesNotChangeIt()
    {
        var acceptance = AcceptanceSamples.Submitted();

        // Домен не трогает ревизию: иначе после каждой правки в памяти
        // получалось бы новое значение, и проверка в WHERE снова совпадала бы.
        Assert.Equal(1, acceptance.Revision);

        acceptance.AcceptRevision(7);

        Assert.Equal(7, acceptance.Revision);
    }
}
