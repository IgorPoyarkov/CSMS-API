using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Машина состояний записи о привлечении СМП: статус меняется только методами
/// домена, каждый переход пишет журнал и заново прогоняет правила.
/// </summary>
public class ContractSmpWorkflowTests
{
    [Fact]
    public void Create_StartsFromSubmittedAndWritesEvent()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);

        Assert.Equal(ContractSmpStatus.Submitted, record.Status);
        Assert.Equal(1, record.Revision);
        Assert.Equal(ContractSmpRecordSamples.Now, record.SubmittedAt);
        Assert.Single(record.Events);
        Assert.Equal("Поданы", record.Events[0].Action);
        Assert.DoesNotContain(record.Validate(), issue => issue.IsError);
    }

    [Fact]
    public void Accept_RequiresSupplierAndCustomerConfirmation()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract() with
            {
                CustomerConfirmedAt = null,
                CustomerConfirmedBy = null,
                CustomerConfirmedDocument = null,
            },
            ContractSmpRecordSamples.Now);

        var error = Assert.Throws<DomainException>(() =>
            record.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now));

        Assert.Contains("подтверждение", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ContractSmpStatus.Submitted, record.Status);
    }

    [Fact]
    public void Reject_AndResubmit_ReturnRecordToWork()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);

        record.Reject(ContractSmpRecordSamples.Actor, "Нет декларации о принадлежности к СМП.",
            ContractSmpRecordSamples.Now.AddMinutes(10));

        Assert.Equal(ContractSmpStatus.Rejected, record.Status);
        Assert.Equal("Нет декларации о принадлежности к СМП.", record.RejectedReason);

        record.Resubmit(ContractSmpRecordSamples.SupplierActor, ContractSmpRecordSamples.Now.AddMinutes(20));

        Assert.Equal(ContractSmpStatus.Submitted, record.Status);
        Assert.Null(record.RejectedReason);
        Assert.Equal(3, record.Events.Count);
    }

    [Fact]
    public void Accept_AndPushToEis_FixPublishRequisites()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);

        record.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now.AddMinutes(10));
        record.PushToEis(
            ContractSmpRecordSamples.Actor,
            new DateOnly(2026, 10, 9),
            "rec-123",
            ContractSmpRecordSamples.Now.AddMinutes(20));

        Assert.Equal(ContractSmpStatus.PushedToEis, record.Status);
        Assert.Equal(new DateOnly(2026, 10, 9), record.EisPublishDate);
        Assert.Equal("rec-123", record.EisRecordNumber);
        Assert.Equal(ContractSmpRecordSamples.Actor, record.AcceptedBy);
        Assert.Equal(3, record.Events.Count);
        Assert.False(record.CanDelete);
    }

    [Fact]
    public void PushToEis_IsImpossibleFromSubmittedStatus()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);

        var error = Assert.Throws<DomainException>(() => record.PushToEis(
            ContractSmpRecordSamples.Actor,
            new DateOnly(2026, 10, 9),
            "rec-123",
            ContractSmpRecordSamples.Now));

        Assert.Contains("Приняты заказчиком", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_IsBlockedAfterAcceptance()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);
        record.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now.AddMinutes(10));

        var error = Assert.Throws<DomainException>(() => record.Update(
            ContractSmpRecordSamples.SubContract() with { Amount = 2_000_000m },
            ContractSmpRecordSamples.Actor,
            ContractSmpRecordSamples.Now.AddMinutes(20)));

        Assert.Contains("Приняты заказчиком", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Document_AddAndRemove_AreJournaled()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract(),
            ContractSmpRecordSamples.Now);

        var document = record.AddDocument(
            ContractSmpDocumentType.Declaration,
            "Декларация ООО «РосДорМонтаж»",
            "Декларация о принадлежности к СМП от 06.10.2026",
            ContractSmpRecordSamples.SupplierActor,
            ContractSmpRecordSamples.Now.AddMinutes(5));

        Assert.True(document.MatchesHash());
        Assert.Single(record.Documents);

        record.RemoveDocument(document.Id, ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now.AddMinutes(10));

        Assert.Empty(record.Documents);
        Assert.Equal(3, record.Events.Count);
    }

    [Fact]
    public void Restore_ShowsHashMismatch()
    {
        var document = ContractSmpDocument.Create(
            ContractSmpDocumentType.Payment,
            "Платёжное поручение №18",
            "Платёж от 20.10.2026",
            ContractSmpRecordSamples.SupplierActor,
            ContractSmpRecordSamples.Now);

        var restored = ContractSmpDocument.Restore(
            document.Id,
            document.Type,
            document.Title,
            document.Content,
            document.Version,
            "deadbeef",
            document.CreatedBy,
            document.CreatedAt);

        Assert.False(restored.MatchesHash());

        var record = ContractSmpRecord.Restore(
            Guid.NewGuid(),
            ContractSmpRecordSamples.SubContract(),
            ContractSmpStatus.Submitted,
            [restored],
            [],
            ContractSmpRecordSamples.Now,
            ContractSmpRecordSamples.Now,
            ContractSmpRecordSamples.Now,
            null, null, null, null, null, null, null, null,
            1);

        Assert.Contains(record.Validate(), issue => issue.Field == "documents" && issue.IsError);
    }
}
