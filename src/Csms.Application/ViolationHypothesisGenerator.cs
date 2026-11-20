namespace Csms.Application;

/// <summary>
/// Этап 1: детерминированная семантическая инверсия обязанности.
/// Из позитивной формулировки должного действия строятся гипотезы возможного
/// нарушения — без LLM, чтобы поиск и аудит воспроизводились побайтово.
/// </summary>
public sealed class ViolationHypothesisGenerator : IViolationHypothesisGenerator
{
    public IReadOnlyList<ViolationHypothesis> Generate(DutyInput duty)
    {
        ArgumentNullException.ThrowIfNull(duty);

        var dutyId = duty.Identifiers.DutyId;
        var subject = Text(duty.Subject, "Уполномоченное лицо");
        var action = Text(duty.Statement, "исполнить обязанность");
        var objectText = Text(duty.Object, "объект обязанности");
        var deadline = Text(duty.Deadline, "установленный срок");
        var condition = string.IsNullOrWhiteSpace(duty.Condition)
            ? string.Empty
            : $" Условие: {duty.Condition.Trim()}.";

        return
        [
            new ViolationHypothesis(
                "h1",
                ViolationKind.Omission,
                $"Полное неисполнение или бездействие: {subject} не совершил(а) действие «{action}» " +
                $"в отношении {objectText}.{condition}",
                dutyId),
            new ViolationHypothesis(
                "h2",
                ViolationKind.DeadlineViolation,
                $"Нарушение срока исполнения: {subject} совершил(а) действие «{action}» " +
                $"в отношении {objectText} позже срока «{deadline}».{condition}",
                dutyId),
            new ViolationHypothesis(
                "h3",
                ViolationKind.ProcedureOrFormViolation,
                $"Нарушение порядка, формы или состава сведений: {subject} исполнил(а) «{action}» " +
                $"в отношении {objectText} с нарушением установленного порядка, формы или неполного состава сведений.{condition}",
                dutyId),
            new ViolationHypothesis(
                "h4",
                ViolationKind.SubjectOrAuthorityViolation,
                $"Нарушение субъектом или превышение полномочий: действие «{action}» в отношении " +
                $"{objectText} совершено лицом, не уполномоченным либо с превышением полномочий " +
                $"(должен был действовать {subject}).{condition}",
                dutyId),
        ];
    }

    private static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
