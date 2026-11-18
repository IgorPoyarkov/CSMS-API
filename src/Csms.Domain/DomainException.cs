namespace Csms.Domain;

/// <summary>
/// Нарушено бизнес-правило: запрос синтаксически верен, но сейчас недопустим.
/// API отдаёт это как 409 с описанием на русском — человеку, а не разработчику.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }
}
