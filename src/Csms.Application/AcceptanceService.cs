using Csms.Domain;

namespace Csms.Application;

/// <summary>
/// Сценарии электронного актирования: приёмка, расхождения, мотивированный
/// отказ и регистрация в ЕИС.
///
/// Слой не знает про HTTP и базу: только домен, хранилище и часы. Поэтому
/// правила ст. 94 проверяются одинаково и в API, и в тестах, и при повторном
/// открытии карточки.
/// </summary>
public sealed class AcceptanceService(
    IAcceptanceRepository repository,
    TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public Task<IReadOnlyList<ContractAcceptance>> ListAsync(
        string? organizationId,
        string? contractId,
        AcceptanceStatus? status,
        CancellationToken cancellationToken) =>
        repository.ListAsync(organizationId, contractId, status, cancellationToken);

    public async Task<ContractAcceptance> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.FindAsync(id, cancellationToken)
        ?? throw new DomainException($"Документ о приёмке {id} не найден.");

    /// <summary>
    /// Проверить условия без сохранения — для формы и предпросмотра. Позиции
    /// в черновике ещё не сформированы, поэтому проверяются сами реквизиты,
    /// а не комплектность документа.
    /// </summary>
    public IReadOnlyList<AcceptanceIssue> Validate(AcceptanceInput input) =>
        AcceptanceRules.Validate(input, [], [], AcceptanceStatus.Draft);

    public async Task<ContractAcceptance> CreateAsync(
        CreateAcceptanceCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = ContractAcceptance.Create(command.Input, Now);

        await repository.AddAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> UpdateAsync(
        UpdateAcceptanceCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.UpdateDraft(command.Input, command.Actor, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> AddPositionAsync(
        AddAcceptancePositionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.AddPosition(
            command.Name,
            command.Okpd2,
            command.Unit,
            command.Quantity,
            command.UnitPrice,
            command.VatRate,
            command.CountryOrigin,
            command.RegistryNumber,
            command.Actor,
            Now);

        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> RemovePositionAsync(
        RemoveAcceptancePositionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.RemovePosition(command.PositionId, command.Actor, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> AddDefectAsync(
        AddAcceptanceDefectCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.AddDefect(
            command.PositionId,
            command.Description,
            command.Quantity,
            command.Requirement,
            command.Actor,
            Now);

        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> RemoveDefectAsync(
        RemoveAcceptanceDefectCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.RemoveDefect(command.DefectId, command.Actor, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> SubmitAsync(
        AcceptanceActionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.Submit(command.Actor, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> AcceptAsync(
        AcceptAcceptanceCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.Accept(command.Result, command.Actor, command.Comment, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> RefuseAsync(
        AcceptanceActionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.Refuse(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> ReturnAsync(
        AcceptanceActionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.ReturnForRevision(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> RegisterAsync(
        RegisterAcceptanceCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.RegisterInEis(
            command.Actor,
            command.RegistrationNumber,
            command.RegistrationDate,
            Now);

        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    public async Task<ContractAcceptance> CancelAsync(
        AcceptanceActionCommand command,
        CancellationToken cancellationToken)
    {
        var acceptance = await GetAsync(command.Id, cancellationToken);

        acceptance.Cancel(command.Actor, command.Comment ?? string.Empty, Now);
        await SaveAsync(acceptance, cancellationToken);

        return acceptance;
    }

    private async Task SaveAsync(ContractAcceptance acceptance, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveAsync(acceptance, cancellationToken);
        }
        catch (AcceptanceStoreConflictException)
        {
            throw new DomainException(
                "Документ уже изменён в другом окне или другим действием. Обновите страницу и повторите.");
        }
    }
}
