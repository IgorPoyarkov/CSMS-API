namespace Csms.Application;

/// <summary>
/// Хранилище не смогло сохранить пакет: строка уже изменена в другом потоке
/// (например, два нажатия «Отправить» одновременно).
///
/// Прикладной слой переводит это в <see cref="DomainException"/> со словами для
/// человека: само по себе исключение — техническая деталь БД, а показывается
/// человеку русский текст.
/// </summary>
public sealed class EisPackageStoreConflictException(string message, Exception inner)
    : Exception(message, inner);
