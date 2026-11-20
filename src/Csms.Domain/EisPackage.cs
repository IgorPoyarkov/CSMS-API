using System.Security.Cryptography;
using System.Text;

namespace Csms.Domain;

/// <summary>Запись журнала пакета: когда и почему сменился статус.</summary>
public sealed record EisPackageEvent(
    DateTimeOffset At,
    EisPackageStatus From,
    EisPackageStatus To,
    string Comment);

/// <summary>
/// Пакет сведений для ЕИС: содержимое, подпись, статус и журнал.
///
/// У класса нет публичных сеттеров намеренно. Статус меняется только методами,
/// и каждый проверяет допустимость перехода. Это дешёвая защита от самой
/// дорогой ошибки в обмене: пакет, который «выглядит отправленным», но ЕИС его
/// не принимала, или подпись, поставленная под другим содержимым.
/// </summary>
public sealed class EisPackage
{
    private readonly List<EisPackageEvent> _history = [];
    private readonly List<FlkError> _flkErrors = [];

    private EisPackage(
        Guid id,
        EisDocumentType documentType,
        string contractEisNumber,
        string ikz,
        string payloadXml,
        string payloadSchemaVersion,
        DateTimeOffset createdAt,
        EisPackageSource? source,
        EisCustomerSnapshot? customer)
    {
        Id = id;
        DocumentType = documentType;
        ContractEisNumber = contractEisNumber;
        Ikz = ikz;
        PayloadXml = payloadXml;
        PayloadSchemaVersion = payloadSchemaVersion;
        PayloadSha256 = Sha256Hex(payloadXml);
        Source = source;
        Customer = customer;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = EisPackageStatus.Draft;

        _history.Add(new EisPackageEvent(createdAt, EisPackageStatus.Draft, EisPackageStatus.Draft, "Пакет создан"));
    }

    public Guid Id { get; }

    public EisDocumentType DocumentType { get; }

    /// <summary>Реестровый номер контракта в ЕИС — ключ привязки сведений.</summary>
    public string ContractEisNumber { get; }

    public string Ikz { get; }

    /// <summary>Содержимое, которое подписывается и уходит в ЕИС.</summary>
    public string PayloadXml { get; private set; }

    /// <summary>Версия схемы, по которой собран <see cref="PayloadXml"/>.</summary>
    public string PayloadSchemaVersion { get; private set; }

    /// <summary>
    /// Документ CSMS, из которого собран пакет. Фиксируется при создании и не
    /// меняется последующими действиями над пакетом.
    /// </summary>
    public EisPackageSource? Source { get; }

    /// <summary>Снимок реквизитов заказчика на момент создания пакета.</summary>
    public EisCustomerSnapshot? Customer { get; }

    /// <summary>SHA-256 содержимого: по нему сверяется, что подписали именно этот файл.</summary>
    public string PayloadSha256 { get; private set; }

    /// <summary>Открепленная подпись в base64 — ровно то, что вернул плагин.</summary>
    public string? SignatureBase64 { get; private set; }

    /// <summary>Отпечаток сертификата подписанта: по нему видно, чья это подпись.</summary>
    public string? SignerThumbprint { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }

    /// <summary>Идентификатор пакета в ЕИС; появляется после успешной отправки.</summary>
    public string? EisGuid { get; private set; }

    public EisPackageStatus Status { get; private set; }

    /// <summary>Причина транспортной ошибки — для человека, не для лога.</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<FlkError> FlkErrors => _flkErrors;

    public IReadOnlyList<EisPackageEvent> History => _history;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static EisPackage Create(
        EisDocumentType documentType,
        string? contractEisNumber,
        string? ikz,
        string payloadXml,
        string payloadSchemaVersion,
        DateTimeOffset now,
        EisPackageSource? source = null,
        EisCustomerSnapshot? customer = null)
    {
        var number = EisRegistryNumber.Normalize(contractEisNumber);

        // Извещение и проект контракта живут до заключения контракта, поэтому
        // реестрового номера у них ещё нет: для этих типов он не требуется.
        var requiresContractNumber = documentType is
            EisDocumentType.SmpAttraction or EisDocumentType.ContractExecution;

        if (requiresContractNumber && !EisRegistryNumber.IsValid(number))
        {
            throw new DomainException(
                $"Реестровый номер контракта в ЕИС должен содержать {EisRegistryNumber.Length} цифр, " +
                $"получено «{number}». Без него ЕИС не поймёт, к какому контракту относятся сведения.");
        }

        if (string.IsNullOrWhiteSpace(payloadXml))
        {
            throw new DomainException("Пустое содержимое пакета: подписывать и отправлять нечего.");
        }

        return new EisPackage(
            Guid.NewGuid(),
            documentType,
            number,
            (ikz ?? string.Empty).Trim(),
            payloadXml,
            payloadSchemaVersion,
            now,
            NormalizeSource(source),
            NormalizeCustomer(customer));
    }

    /// <summary>
    /// Проверить ссылку на источник: пустой documentId делает её бесполезной,
    /// а неизвестный вид документа не даст фронту найти документ-источник.
    /// </summary>
    private static EisPackageSource? NormalizeSource(EisPackageSource? source)
    {
        if (source is null)
        {
            return null;
        }

        if (!Enum.IsDefined(source.DocumentKind))
        {
            throw new DomainException(
                $"Неизвестный вид документа-источника пакета ЕИС: {source.DocumentKind}.");
        }

        var id = Trim(source.DocumentId, 128, "source.documentId");

        if (string.IsNullOrEmpty(id))
        {
            throw new DomainException(
                "Не заполнен source.documentId: без идентификатора документа-источника пакет нельзя пересобрать.");
        }

        return source with
        {
            DocumentId = id!,
            DocumentNumber = Trim(source.DocumentNumber, 128, "source.documentNumber"),
        };
    }

    private static EisCustomerSnapshot? NormalizeCustomer(EisCustomerSnapshot? customer) =>
        customer is null
            ? null
            : customer with
            {
                FullName = Trim(customer.FullName, 512, "customer.fullName"),
                ShortName = Trim(customer.ShortName, 256, "customer.shortName"),
                Inn = Trim(customer.Inn, 32, "customer.inn"),
                Kpp = Trim(customer.Kpp, 32, "customer.kpp"),
                Okopf = Trim(customer.Okopf, 32, "customer.okopf"),
                Okfs = Trim(customer.Okfs, 32, "customer.okfs"),
                Oktmo = Trim(customer.Oktmo, 32, "customer.oktmo"),
                OktmoPpo = Trim(customer.OktmoPpo, 32, "customer.oktmoPpo"),
                RegNumber = Trim(customer.RegNumber, 32, "customer.regNumber"),
            };

    private static string? Trim(string? value, int maxLength, string field)
    {
        var text = value?.Trim();

        if (text is not null && text.Length > maxLength)
        {
            throw new DomainException(
                $"Поле «{field}» длиннее {maxLength} символов, разрешённых для пакета ЕИС.");
        }

        return text is null || text.Length == 0 ? null : text;
    }

    /// <summary>Начать подписание. С этого момента содержимое менять нельзя.</summary>
    public void BeginSigning(DateTimeOffset now)
    {
        EnsureStatus("начать подписание", EisPackageStatus.Draft);
        Transition(EisPackageStatus.Signing, now, "Начато подписание");
    }

    /// <summary>
    /// Приложить подпись, полученную от плагина на рабочем месте.
    ///
    /// Если передан хеш подписанного содержимого, он сверяется с текущим: так
    /// ловится случай «подписали один файл, отправляем другой», который иначе
    /// всплывёт только отказом ФЛК в ЕИС.
    /// </summary>
    public void AttachSignature(
        string? signatureBase64,
        string? signerThumbprint,
        string? signedContentSha256,
        DateTimeOffset now)
    {
        EnsureStatus("приложить подпись", EisPackageStatus.Signing);

        var signature = (signatureBase64 ?? string.Empty).Trim();

        if (signature.Length == 0)
        {
            throw new DomainException("Пустая подпись: плагин ничего не вернул.");
        }

        var signedHash = (signedContentSha256 ?? string.Empty).Trim();

        if (signedHash.Length > 0 && !string.Equals(signedHash, PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(
                "Подпись поставлена под другим содержимым: хеш подписанного не совпадает с текущим пакетом. " +
                "Повторите подписание — пакет изменился после того, как был подписан.");
        }

        SignatureBase64 = signature;
        SignerThumbprint = (signerThumbprint ?? string.Empty).Trim();
        SignedAt = now;

        Transition(EisPackageStatus.Signed, now, "Подпись приложена");
    }

    /// <summary>Передать подписанный пакет в транспорт ЕИС.</summary>
    public void MarkSent(string? eisGuid, DateTimeOffset now)
    {
        EnsureStatus("передать пакет в ЕИС", EisPackageStatus.Signed, EisPackageStatus.SendFailed);

        if (SignatureBase64 is null)
        {
            throw new DomainException("Нельзя отправить пакет без подписи.");
        }

        EisGuid = string.IsNullOrWhiteSpace(eisGuid) ? null : eisGuid.Trim();
        LastError = null;

        Transition(EisPackageStatus.SentToEis, now, "Пакет передан в транспорт ЕИС");
    }

    /// <summary>ЕИС подтвердила получение и начала проверку ФЛК.</summary>
    public void MarkFlkChecking(DateTimeOffset now)
    {
        EnsureStatus("перевести пакет в проверку ФЛК", EisPackageStatus.SentToEis, EisPackageStatus.FlkChecking);

        if (Status == EisPackageStatus.FlkChecking)
        {
            return;
        }

        Transition(EisPackageStatus.FlkChecking, now, "ЕИС проверяет пакет (ФЛК)");
    }

    public void MarkAccepted(DateTimeOffset now)
    {
        EnsureStatus("зафиксировать приём пакета", EisPackageStatus.FlkChecking);
        _flkErrors.Clear();
        Transition(EisPackageStatus.AcceptedInLk, now, "Пакет принят ЕИС");
    }

    public void MarkRejected(IEnumerable<FlkError>? errors, DateTimeOffset now)
    {
        EnsureStatus("зафиксировать отказ ФЛК", EisPackageStatus.FlkChecking);

        _flkErrors.Clear();
        _flkErrors.AddRange(errors ?? []);

        // Отказ без описания — тоже отказ: пустой список ошибок нельзя
        // показывать человеку как «всё хорошо»
        if (_flkErrors.Count == 0)
        {
            _flkErrors.Add(new FlkError(
                "FLK_UNKNOWN",
                "ЕИС отклонила пакет, но не вернула описание ошибок.",
                Hint: "Смотрите протокол проверки в личном кабинете ЕИС."));
        }

        Transition(EisPackageStatus.RejectedByFlk, now, $"ФЛК вернула {_flkErrors.Count} ошибок");
    }

    /// <summary>До ЕИС не дошли. Отправку можно повторить тем же пакетом.</summary>
    public void MarkSendFailed(string? reason, DateTimeOffset now)
    {
        // SendFailed тоже допустим: повторная отправка может упасть снова,
        // и это должно обновить причину, а не превратиться в исключение
        EnsureStatus(
            "зафиксировать транспортную ошибку",
            EisPackageStatus.Signed,
            EisPackageStatus.SentToEis,
            EisPackageStatus.SendFailed);

        LastError = string.IsNullOrWhiteSpace(reason) ? "Транспорт не вернул причину" : reason.Trim();
        Transition(EisPackageStatus.SendFailed, now, "Транспортная ошибка");
    }

    /// <summary>
    /// Вернуть отклоненный пакет в работу: снять подпись и ошибки, чтобы
    /// исправить данные и отправить заново.
    /// </summary>
    public void Revise(DateTimeOffset now)
    {
        EnsureStatus(
            "вернуть пакет в работу",
            EisPackageStatus.RejectedByFlk,
            EisPackageStatus.SendFailed);

        SignatureBase64 = null;
        SignerThumbprint = null;
        SignedAt = null;
        LastError = null;
        _flkErrors.Clear();

        Transition(EisPackageStatus.Draft, now, "Пакет возвращён в работу");
    }

    /// <summary>
    /// Заменить содержимое черновика. После подписания это запрещено: иначе
    /// подпись перестанет соответствовать документу.
    /// </summary>
    public void UpdatePayload(string payloadXml, string payloadSchemaVersion, DateTimeOffset now)
    {
        EnsureStatus("изменить содержимое пакета", EisPackageStatus.Draft);

        if (string.IsNullOrWhiteSpace(payloadXml))
        {
            throw new DomainException("Пустое содержимое пакета: обновлять нечего.");
        }

        PayloadXml = payloadXml;
        PayloadSchemaVersion = payloadSchemaVersion;
        PayloadSha256 = Sha256Hex(payloadXml);

        UpdatedAt = now;
        _history.Add(new EisPackageEvent(now, Status, Status, "Содержимое пакета обновлено"));
    }

    /// <summary>
    /// Восстановить пакет из хранилища.
    ///
    /// Нужен только репозиторию: публичный конструктор создаёт новый черновик,
    /// а пакет с подписью и журналом из БД так не собрать. Переходы статуса
    /// по-прежнему возможны только методами с проверками — этот путь лишь
    /// возвращает уже случившееся состояние, а не «назначает» его.
    /// </summary>
    public static EisPackage Restore(
        Guid id,
        EisDocumentType documentType,
        string contractEisNumber,
        string ikz,
        string payloadXml,
        string payloadSchemaVersion,
        string? signatureBase64,
        string? signerThumbprint,
        DateTimeOffset? signedAt,
        string? eisGuid,
        EisPackageStatus status,
        string? lastError,
        IReadOnlyList<FlkError> flkErrors,
        IReadOnlyList<EisPackageEvent> history,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        EisPackageSource? source = null,
        EisCustomerSnapshot? customer = null)
    {
        var package = new EisPackage(
            id,
            documentType,
            contractEisNumber,
            ikz,
            payloadXml,
            payloadSchemaVersion,
            createdAt,
            source,
            customer);

        package.PayloadSchemaVersion = payloadSchemaVersion;
        package.PayloadSha256 = Sha256Hex(payloadXml);
        package.SignatureBase64 = signatureBase64;
        package.SignerThumbprint = signerThumbprint;
        package.SignedAt = signedAt;
        package.EisGuid = eisGuid;
        package.Status = status;
        package.LastError = lastError;
        package.UpdatedAt = updatedAt;

        package._flkErrors.AddRange(flkErrors);
        package._history.Clear();
        package._history.AddRange(history);

        return package;
    }

    private void EnsureStatus(string action, params EisPackageStatus[] allowed)
    {
        if (allowed.Contains(Status))
        {
            return;
        }

        var expected = string.Join(" или ", allowed.Select(status => $"«{status.Ru()}»"));

        throw new DomainException(
            $"Нельзя {action}: пакет в статусе «{Status.Ru()}», а требуется {expected}.");
    }

    private void Transition(EisPackageStatus to, DateTimeOffset now, string comment)
    {
        var from = Status;

        Status = to;
        UpdatedAt = now;
        _history.Add(new EisPackageEvent(now, from, to, comment));
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
