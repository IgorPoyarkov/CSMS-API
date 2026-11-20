namespace Csms.Application;

/// <summary>
/// Версии наборов функций по документам. Версии не перезаписываются:
/// подтверждение меняет_status конкретной записи активной версии.
/// </summary>
public interface IDocumentFunctionStore
{
    Task<IReadOnlyList<DocumentFunctionSet>> GetVersionsAsync(
        string sha256, CancellationToken cancellationToken = default);

    Task<DocumentFunctionSet?> GetActiveAsync(
        string sha256, CancellationToken cancellationToken = default);

    Task<DocumentFunctionSet> PublishAsync(
        DocumentFunctionSet set, CancellationToken cancellationToken = default);

    Task<DocumentFunctionItem?> GetFunctionAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<DocumentFunctionItem?> VerifyAsync(
        Guid id, string status, string? note, CancellationToken cancellationToken = default);
}
