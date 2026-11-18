namespace Csms.Domain;

/// <summary>
/// Ошибка форматно-логического контроля (ФЛК) со стороны ЕИС.
///
/// Храним как есть, не переводим в свои коды: <c>Code</c> нужен, чтобы найти
/// расшифровку в регламенте ЕИС, <c>Field</c> — чтобы подсветить поле в
/// интерфейсе, <c>Hint</c> — что именно сделать.
/// </summary>
public sealed record FlkError(
    string Code,
    string Message,
    string? Field = null,
    string? Hint = null);
