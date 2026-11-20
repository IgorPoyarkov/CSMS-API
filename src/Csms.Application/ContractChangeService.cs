using Csms.Domain;

namespace Csms.Application;

/// <summary>
/// Сценарии изменения и расторжения контракта.
///
/// Слой не знает про HTTP и базу: только домен, хранилище и часы. Поэтому
/// правила ст. 95 проверяются одинаково и в API, и в тестах, и при повторном
/// открытии карточки.
/// </summary>
public sealed class ContractChangeService(
    IContractChangeRepository repository,
    TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public Task<IReadOnlyList<ContractChange>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken) =>
        repository.ListAsync(organizationId, contractId, cancellationToken);

    public async Task<ContractChange> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.FindAsync(id, cancellationToken)
        ?? throw new DomainException($"Документ {id} не найден.");

    /// <summary>
    /// Проверить условия без сохранения — для формы и предпросмотра.
    /// Документы в черновике ещё не сформированы, поэтому передаётся пустой
    /// список: проверяются сами условия, а не комплектность.
    /// </summary>
    public IReadOnlyList<ContractChangeIssue> Validate(ContractChangeInput input) =>
        ContractChangeRules.Validate(input, [], ContractChangeStatus.Draft);

    public async Task<ContractChange> CreateAsync(
        CreateContractChangeCommand command,
        CancellationToken cancellationToken)
    {
        var change = ContractChange.Create(command.Input, Now);

        await repository.AddAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> UpdateAsync(
        UpdateContractChangeCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.UpdateDraft(command.Input, command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> AddDocumentAsync(
        AddContractChangeDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.AddDocument(command.Type, command.Title, command.Content, command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> RemoveDocumentAsync(
        RemoveContractChangeDocumentCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.RemoveDocument(command.DocumentId, command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> SubmitAsync(
        ContractChangeActionCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.Submit(command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> ApproveAsync(
        ContractChangeActionCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.Approve(command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> RejectAsync(
        ContractChangeActionCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.Reject(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> SignAsync(
        ContractChangeActionCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.Sign(command.Actor, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> RegisterAsync(
        RegisterContractChangeCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.RegisterInEis(
            command.Actor,
            command.RegistrationNumber,
            command.RegistrationDate,
            Now);

        await SaveAsync(change, cancellationToken);

        return change;
    }

    public async Task<ContractChange> CancelAsync(
        ContractChangeActionCommand command,
        CancellationToken cancellationToken)
    {
        var change = await GetAsync(command.Id, cancellationToken);

        change.Cancel(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(change, cancellationToken);

        return change;
    }

    private async Task SaveAsync(ContractChange change, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveAsync(change, cancellationToken);
        }
        catch (ContractChangeStoreConflictException)
        {
            throw new DomainException(
                "Документ уже изменён в другом окне или другим действием. Обновите страницу и повторите.");
        }
    }
}
