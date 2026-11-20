using Csms.Domain;

namespace Csms.Application;

/// <summary>
/// Сценарии внесения сведений о привлечении СМП/СОНКО. Слой не знает про HTTP
/// и базу: только домен, хранилище и часы. Поэтому правила ch. 30 44-ФЗ и
/// ПП РФ № 1466 одинаковы в API, тестах и при повторном открытии карточки.
/// </summary>
public sealed class ContractSmpRecordService(
    IContractSmpRecordRepository repository,
    TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public Task<IReadOnlyList<ContractSmpRecord>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken) =>
        repository.ListAsync(organizationId, contractId, cancellationToken);

    public async Task<ContractSmpRecord> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.FindAsync(id, cancellationToken)
        ?? throw new DomainException($"Запись {id} не найдена.");

    /// <summary>Проверить условия без сохранения — для формы и предпросмотра.</summary>
    public IReadOnlyList<ContractSmpIssue> Validate(ContractSmpRecordInput input) =>
        ContractSmpRules.Validate(input, [], ContractSmpStatus.Submitted, Now);

    /// <summary>Контроль объёма attracting по всем записям контракта.</summary>
    public async Task<SmpVolumeResult> CalculateVolumeAsync(
        string contractId,
        decimal contractPrice,
        decimal? requiredPercent,
        CancellationToken cancellationToken)
    {
        var records = await repository.ListAsync(null, contractId, cancellationToken);
        return ContractSmpVolume.Calculate(records, contractPrice, requiredPercent);
    }

    public async Task<ContractSmpRecord> CreateAsync(
        CreateContractSmpRecordCommand command,
        CancellationToken cancellationToken)
    {
        var record = ContractSmpRecord.Create(command.Input, Now);

        await repository.AddAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> UpdateAsync(
        UpdateContractSmpRecordCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.Update(command.Input, command.Actor, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> AddDocumentAsync(
        AddContractSmpDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.AddDocument(command.Type, command.Title, command.Content, command.Actor, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> RemoveDocumentAsync(
        RemoveContractSmpDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.RemoveDocument(command.DocumentId, command.Actor, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> AcceptAsync(
        ContractSmpRecordActionCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.Accept(command.Actor, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> RejectAsync(
        ContractSmpRecordActionCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.Reject(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> ResubmitAsync(
        ContractSmpRecordActionCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.Resubmit(command.Actor, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task<ContractSmpRecord> PushToEisAsync(
        PushContractSmpRecordCommand command,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(command.Id, cancellationToken);

        record.PushToEis(command.Actor, command.PublishDate, command.RecordNumber, Now);
        await SaveAsync(record, cancellationToken);

        return record;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await GetAsync(id, cancellationToken);

        if (!record.CanDelete)
        {
            throw new DomainException(
                "Принятые и выгруженные в ЕИС сведения удалить нельзя: сначала отклоните запись.");
        }

        try
        {
            await repository.DeleteAsync(record, cancellationToken);
        }
        catch (ContractSmpStoreConflictException)
        {
            throw new DomainException(
                "Запись уже изменена в другом окне. Обновите страницу и повторите.");
        }
    }

    private async Task SaveAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveAsync(record, cancellationToken);
        }
        catch (ContractSmpStoreConflictException)
        {
            throw new DomainException(
                "Сведения уже изменены в другом окне или другим действием. Обновите страницу и повторите.");
        }
    }
}
