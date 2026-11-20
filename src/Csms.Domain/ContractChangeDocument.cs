using System.Security.Cryptography;
using System.Text;

namespace Csms.Domain;

/// <summary>
/// Документ по изменению или расторжению: соглашение, уведомление, решение,
/// судебный акт или приложение.
///
/// Хранится текст и хеш содержимого. Хеш нужен не «для красоты»: по нему
/// видно, что подписанная версия совпадает с той, которую согласовали, и что
/// документ не переписали после подписания.
/// </summary>
public sealed class ContractChangeDocument
{
    private ContractChangeDocument(
        Guid id,
        ContractChangeDocumentType type,
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

    public ContractChangeDocumentType Type { get; private set; }

    public string Title { get; private set; }

    /// <summary>Текст документа. Пустой текст недопустим — это не документ.</summary>
    public string Content { get; private set; }

    public int Version { get; private set; }

    public string Sha256 { get; private set; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ContractChangeDocument Create(
        ContractChangeDocumentType type,
        string title,
        string content,
        string actor,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException("Пустой документ: заполните текст соглашения, уведомления или решения.");
        }

        return new ContractChangeDocument(
            Guid.NewGuid(),
            type,
            NormalizeTitle(type, title),
            content.Trim(),
            version: 1,
            NormalizeActor(actor),
            now);
    }

    /// <summary>
    /// Новая редакция текста. Версия увеличивается, хеш пересчитывается:
    /// подписывать нужно ровно ту редакцию, которую видели согласующие.
    /// </summary>
    public void UpdateContent(string title, string content, string actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException("Пустой документ: заполните текст перед сохранением.");
        }

        Title = NormalizeTitle(Type, title);
        Content = content.Trim();
        Version++;
        Sha256 = ComputeHash(Content);
    }

    public static ContractChangeDocument Restore(
        Guid id,
        ContractChangeDocumentType type,
        string title,
        string content,
        int version,
        string sha256,
        string createdBy,
        DateTimeOffset createdAt)
    {
        var document = new ContractChangeDocument(id, type, title, content, version, createdBy, createdAt);

        // Хеш из хранилища не пересчитываем: если содержимое и хеш разошлись,
        // это должно быть видно при проверке, а не молча «исправлено»
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            document.Sha256 = sha256;
        }

        return document;
    }

    /// <summary>Совпадает ли текущее содержимое с сохранённым хешем.</summary>
    public bool MatchesHash() =>
        string.Equals(Sha256, ComputeHash(Content), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(ContractChangeDocumentType type, string? title) =>
        string.IsNullOrWhiteSpace(title) ? type.Ru() : title.Trim();

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? "Система" : actor.Trim();

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Событие аудита по документу изменения/расторжения.</summary>
public sealed record ContractChangeEvent(
    DateTimeOffset At,
    ContractChangeStatus From,
    ContractChangeStatus To,
    string Action,
    string Comment,
    string Actor);
