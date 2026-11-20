using System.Security.Cryptography;
using System.Text;

namespace Csms.Domain;

/// <summary>
/// Документ-основание по записи о привлечении СМП: копия договора, декларация,
/// акт приёмки или платёжное поручение. Хранится текст (например, base64) и
/// хеш: по нему видно, что выгруженная в ЕИС редакция не переписана afterwards.
/// </summary>
public sealed class ContractSmpDocument
{
    private ContractSmpDocument(
        Guid id,
        ContractSmpDocumentType type,
        string title,
        string content,
        int version,
        string createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        Type = type;
        Title = title;
        Content = content;
        Version = version;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        Sha256 = ComputeHash(content);
    }

    public Guid Id { get; }

    public ContractSmpDocumentType Type { get; }

    public string Title { get; private set; }

    public string Content { get; private set; }

    public int Version { get; private set; }

    public string Sha256 { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ContractSmpDocument Create(
        ContractSmpDocumentType type,
        string title,
        string content,
        string actor,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException("Пустой документ: заполните содержимое перед сохранением.");
        }

        return new ContractSmpDocument(
            Guid.NewGuid(),
            type,
            string.IsNullOrWhiteSpace(title) ? type.Ru() : title.Trim(),
            content.Trim(),
            version: 1,
            string.IsNullOrWhiteSpace(actor) ? "Система" : actor.Trim(),
            now);
    }

    /// <summary>
    /// Восстановление из хранилища. Хеш не пересчитываем: если содержимое и
    /// хеш разошлись, это должно быть видно проверкой, а не «исправлено» молча.
    /// </summary>
    public static ContractSmpDocument Restore(
        Guid id,
        ContractSmpDocumentType type,
        string title,
        string content,
        int version,
        string sha256,
        string createdBy,
        DateTimeOffset createdAt)
    {
        var document = new ContractSmpDocument(id, type, title, content, version, createdBy, createdAt);

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            document.Sha256 = sha256;
        }

        return document;
    }

    public bool MatchesHash() =>
        string.Equals(Sha256, ComputeHash(Content), StringComparison.OrdinalIgnoreCase);

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Событие журнала записи о привлечении СМП.</summary>
public sealed record ContractSmpEvent(
    DateTimeOffset At,
    ContractSmpStatus From,
    ContractSmpStatus To,
    string Action,
    string Comment,
    string Actor);
