namespace Csms.Domain;

/// <summary>
/// Реестровый номер контракта в ЕИС — 19 цифр.
///
/// Это не «одно из полей», а ключ привязки: именно по нему ЕИС понимает, к
/// какому контракту относятся сведения. Без него пакет не собрать, поэтому
/// формат проверяется на входе, а не в момент отправки.
/// </summary>
public static class EisRegistryNumber
{
    public const int Length = 19;

    public static bool IsValid(string? value) =>
        value is { Length: Length } && value.All(char.IsAsciiDigit);

    public static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
