using Csms.Domain;

namespace Csms.Application;

/// <summary>
/// Сценарии работы с пакетами ЕИС.
///
/// Слой ничего не знает про HTTP, базу и SOAP: только домен, хранилище и
/// транспорт. Поэтому все правила можно проверить тестами без сети и без
/// сертификатов.
/// </summary>
public sealed class EisPackageService(
    IEisPackageRepository repository,
    IEisTransport transport,
    IEisDocumentXmlBuilder xmlBuilder,
    IEisSchemaCatalog schemaCatalog,
    TimeProvider clock)
{
    private readonly IEisSchemaCatalog _schemaCatalog = schemaCatalog;

    private DateTimeOffset Now => clock.GetUtcNow();

    public Task<IReadOnlyList<EisPackage>> ListAsync(CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);

    public async Task<EisPackage> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.FindAsync(id, cancellationToken)
        ?? throw new DomainException($"Пакет {id} не найден.");

    public async Task<EisPackage> CreateAsync(
        CreatePackageCommand command,
        CancellationToken cancellationToken)
    {
        // Номер проверяется дважды: здесь — чтобы не собирать XML зря,
        // в домене — чтобы правило нельзя было обойти другим вызывающим кодом
        var normalizedNumber = EisRegistryNumber.Normalize(command.ContractEisNumber);
        var payloadXml = command.PayloadXml?.Trim();

        // Готовый XML извещения или проекта контракта приходит с фронта:
        // его нельзя пересобирать заглушкой, иначе уйдёт не тот документ
        // Ссылка на источник и снимок заказчика приходят вместе с XML: они
        // фиксируются в момент создания и не пересчитываются при повторных
        // действиях над пакетом (Б4).
        var built = string.IsNullOrEmpty(payloadXml)
            ? xmlBuilder.Build(
                command.DocumentType,
                normalizedNumber,
                (command.Ikz ?? string.Empty).Trim())
            : new XmlBuildResult(payloadXml, "17.0");

        EnsureXsdValid(built);

        var package = EisPackage.Create(
            command.DocumentType,
            command.ContractEisNumber,
            command.Ikz,
            built.Xml,
            built.SchemaVersion,
            Now,
            command.Source,
            command.Customer);

        await repository.AddAsync(package, cancellationToken);

        return package;
    }

    /// <summary>Отдать фронту содержимое для подписания.</summary>
    public async Task<SignatureTask> PrepareSignatureAsync(Guid id, CancellationToken cancellationToken)
    {
        var package = await GetAsync(id, cancellationToken);

        if (package.Status == EisPackageStatus.Draft)
        {
            package.BeginSigning(Now);
            await SaveAsync(package, cancellationToken);
        }

        if (package.Status != EisPackageStatus.Signing)
        {
            throw new DomainException(
                $"Подписывать можно только черновик или пакет в подписании, сейчас статус «{package.Status.Ru()}».");
        }

        return new SignatureTask(
            package.PayloadXml,
            package.PayloadSha256,
            package.PayloadSchemaVersion,
            package.PayloadSchemaVersion.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EisPackage> AttachSignatureAsync(
        Guid id,
        AttachSignatureCommand command,
        CancellationToken cancellationToken)
    {
        var package = await GetAsync(id, cancellationToken);

        package.AttachSignature(
            command.SignatureBase64,
            command.SignerThumbprint,
            command.SignedContentSha256,
            Now);

        await SaveAsync(package, cancellationToken);

        return package;
    }

    /// <summary>
    /// Отправить пакет в ЕИС.
    ///
    /// Транспортная ошибка не роняет запрос: она становится состоянием пакета
    /// (<see cref="EisPackageStatus.SendFailed"/>), чтобы её было видно в
    /// интерфейсе и можно было повторить отправку.
    /// </summary>
    public async Task<EisPackage> SendAsync(Guid id, CancellationToken cancellationToken)
    {
        var package = await GetAsync(id, cancellationToken);

        if (package.Status is not (EisPackageStatus.Signed or EisPackageStatus.SendFailed))
        {
            throw new DomainException(
                $"Отправлять можно только подписанный пакет, сейчас статус «{package.Status.Ru()}».");
        }

        try
        {
            var result = await transport.SendAsync(package, cancellationToken);
            package.MarkSent(result.EisGuid, Now);
        }
        catch (EisTransportException error)
        {
            package.MarkSendFailed(error.Message, Now);
            await SaveAsync(package, cancellationToken);

            return package;
        }

        await SaveAsync(package, cancellationToken);

        return package;
    }

    /// <summary>Спросить у ЕИС, чем закончилась проверка ФЛК.</summary>
    public async Task<EisPackage> RefreshStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var package = await GetAsync(id, cancellationToken);

        if (package.Status is not (EisPackageStatus.SentToEis or EisPackageStatus.FlkChecking))
        {
            throw new DomainException(
                $"Запрашивать результат можно только после отправки, сейчас статус «{package.Status.Ru()}».");
        }

        if (string.IsNullOrWhiteSpace(package.EisGuid))
        {
            throw new DomainException(
                "ЕИС не вернула идентификатор пакета (eisGuid): запрашивать результат не по чему.");
        }

        var status = await transport.GetStatusAsync(package, cancellationToken);

        switch (status.Kind)
        {
            case EisStatusKind.InProgress:
                package.MarkFlkChecking(Now);
                break;

            case EisStatusKind.Accepted:
                package.MarkFlkChecking(Now);
                package.MarkAccepted(Now);
                break;

            case EisStatusKind.Rejected:
                package.MarkFlkChecking(Now);
                package.MarkRejected(status.Errors, Now);
                break;

            default:
                throw new DomainException($"ЕИС вернула неизвестное состояние пакета: {status.Kind}.");
        }

        await SaveAsync(package, cancellationToken);

        return package;
    }

    /// <summary>
    /// Проверить XML до сохранения и подписания.
    ///
    /// Для черновика (placeholder) и для версии без загруженных XSD проверка
    /// пропускается: притворяться пройденной валидацией нельзя. Как только
    /// схема версии появится в каталоге, ошибки станут ошибкой запроса, а не
    /// сюрпризом от ФЛК ЕИС после отправки.
    /// </summary>
    private void EnsureXsdValid(XmlBuildResult built)
    {
        if (built.IsPlaceholder || !_schemaCatalog.HasSchema(built.SchemaVersion))
        {
            return;
        }

        var errors = _schemaCatalog.Validate(built.Xml, built.SchemaVersion);

        if (errors.Count == 0)
        {
            return;
        }

        throw new DomainException(
            $"XML не соответствует XSD версии {built.SchemaVersion}: " +
            string.Join("; ", errors.Take(3)) +
            (errors.Count > 3 ? $" … и ещё {errors.Count - 3}." : "."));
    }

    /// <summary>
    /// Сохранить пакет и перевести конфликт хранилища в слова для человека.
    ///
    /// Два окна или два нажатия «Отправить» не должны превращаться в ошибку
    /// сервера: человек видит понятный текст и обновляет страницу.
    /// </summary>
    private async Task SaveAsync(EisPackage package, CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveAsync(package, cancellationToken);
        }
        catch (EisPackageStoreConflictException)
        {
            throw new DomainException(
                "Пакет уже изменён в другом окне или другим действием. Обновите страницу и повторите.");
        }
    }

    /// <summary>
    /// Вернуть отклоненный пакет в работу.
    ///
    /// Раньше здесь безусловно вызывался сборщик заглушки. Для извещения и
    /// проекта контракта, XML которых присылает фронт, это стирало настоящий
    /// документ: повторная подпись ставилась уже под другим содержимым. Теперь
    /// готовый XML сохраняется; пересобираем только пакеты-заглушки, у которых
    /// своего содержимого нет. Исправленный XML можно передать в
    /// <paramref name="payloadXml"/> — он пройдёт ту же XSD-проверку.
    /// </summary>
    public async Task<EisPackage> ReviseAsync(
        Guid id,
        CancellationToken cancellationToken,
        string? payloadXml = null)
    {
        var package = await GetAsync(id, cancellationToken);

        package.Revise(Now);

        var replacement = payloadXml?.Trim();
        var built = !string.IsNullOrEmpty(replacement)
            ? new XmlBuildResult(replacement, package.PayloadSchemaVersion)
            : package.PayloadSchemaVersion.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase)
                ? xmlBuilder.Build(package.DocumentType, package.ContractEisNumber, package.Ikz)
                : new XmlBuildResult(package.PayloadXml, package.PayloadSchemaVersion);

        EnsureXsdValid(built);

        package.UpdatePayload(built.Xml, built.SchemaVersion, Now);

        await SaveAsync(package, cancellationToken);

        return package;
    }
}
