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

        // Порядок гипотез существен: действия идут первыми — основная масса
        // нарушений по 158н связана с ненадлежащим исполнением, а не с
        // полным бездействием. Бездействие сохранено последним: часть составов
        // КоАП (например, ч. 2 ст. 19.5) построена именно на неисполнении.
        return
        [
            new ViolationHypothesis(
                "h1",
                ViolationKind.ProcedureOrFormViolation,
                $"Нарушение порядка, формы или состава сведений при совершении действия: " +
                $"{subject} исполнил(а) «{action}» в отношении {objectText} с нарушением " +
                $"установленного порядка, формы или в неполном составе сведений.{condition}",
                dutyId),
            new ViolationHypothesis(
                "h2",
                ViolationKind.DeadlineViolation,
                $"Нарушение срока исполнения: {subject} совершил(а) действие «{action}» " +
                $"в отношении {objectText} позже срока «{deadline}».{condition}",
                dutyId),
            new ViolationHypothesis(
                "h3",
                ViolationKind.SubjectOrAuthorityViolation,
                $"Нарушение субъектом или превышение полномочий: действие «{action}» в отношении " +
                $"{objectText} совершено лицом, не уполномоченным либо с превышением полномочий " +
                $"(должен был действовать {subject}).{condition}",
                dutyId),
            new ViolationHypothesis(
                "h4",
                ViolationKind.Omission,
                $"Полное неисполнение или бездействие: {subject} не совершил(а) действие «{action}» " +
                $"в отношении {objectText}.{condition}",
                dutyId),
        ];
    }

    private static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
