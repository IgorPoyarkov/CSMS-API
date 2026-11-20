namespace Csms.Domain;

/// <summary>Что это за документ: первичная приёмка или исправление к ней.</summary>
public enum AcceptanceKind
{
    /// <summary>Документ о приёмке поставленного товара, работы или услуги.</summary>
    Primary = 0,

    /// <summary>Корректировочный документ к ранее оформленной приёмке.</summary>
    Correction = 1,
}

/// <summary>
/// Статус документа о приёмке.
///
/// Переходы линейные и проверяются доменом: черновик → направлен заказчику →
/// итог приёмки (принято / принято с расхождениями / мотивированный отказ /
/// возвращено для уточнения) → регистрация в ЕИС. Отменить можно любой
/// незарегистрированный документ.
/// </summary>
public enum AcceptanceStatus
{
    Draft = 0,
    Submitted = 1,
    Accepted = 2,
    AcceptedWithDiscrepancies = 3,
    Refused = 4,
    ReturnedForRevision = 5,
    RegisteredInEis = 6,
    Cancelled = 7,
}

/// <summary>
/// Код итога приёмки по справочнику ЕИС (<c>acceptanceResultCode</c>).
/// Значения совпадают с кодами ЕИС, чтобы не переводить их на границе API.
/// </summary>
public enum AcceptanceResult
{
    /// <summary>0 — возвращено для уточнения.</summary>
    ReturnedForRevision = 0,

    /// <summary>1 — приняты без расхождений (претензий).</summary>
    Accepted = 1,

    /// <summary>2 — приняты с расхождениями (претензией).</summary>
    AcceptedWithDiscrepancies = 2,

    /// <summary>3 — не приняты.</summary>
    Refused = 3,
}

/// <summary>Что заказчик требует по выявленному недостатку.</summary>
public enum AcceptanceDefectRequirement
{
    /// <summary>Заменить товар (результат работы).</summary>
    Replace = 0,

    /// <summary>Устранить недостаток.</summary>
    Repair = 1,

    /// <summary>Уменьшить цену (удержать стоимость).</summary>
    DeductPrice = 2,

    /// <summary>Не принимать позицию.</summary>
    Reject = 3,
}

public static class AcceptanceStatusText
{
    public static string Ru(this AcceptanceStatus status) => status switch
    {
        AcceptanceStatus.Draft => "Черновик",
        AcceptanceStatus.Submitted => "Направлен заказчику",
        AcceptanceStatus.Accepted => "Принято",
        AcceptanceStatus.AcceptedWithDiscrepancies => "Принято с расхождениями",
        AcceptanceStatus.Refused => "Мотивированный отказ",
        AcceptanceStatus.ReturnedForRevision => "Возвращено для уточнения",
        AcceptanceStatus.RegisteredInEis => "Зарегистрирован в ЕИС",
        AcceptanceStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };
}

public static class AcceptanceKindText
{
    public static string Ru(this AcceptanceKind kind) => kind switch
    {
        AcceptanceKind.Primary => "Документ о приёмке",
        AcceptanceKind.Correction => "Корректировочный документ",
        _ => kind.ToString(),
    };
}

public static class AcceptanceResultText
{
    public static string Ru(this AcceptanceResult result) => result switch
    {
        AcceptanceResult.ReturnedForRevision => "Возвращено для уточнения",
        AcceptanceResult.Accepted => "Принято без расхождений",
        AcceptanceResult.AcceptedWithDiscrepancies => "Принято с расхождениями",
        AcceptanceResult.Refused => "Не принято",
        _ => result.ToString(),
    };
}

public static class AcceptanceDefectRequirementText
{
    public static string Ru(this AcceptanceDefectRequirement requirement) => requirement switch
    {
        AcceptanceDefectRequirement.Replace => "Заменить",
        AcceptanceDefectRequirement.Repair => "Устранить недостаток",
        AcceptanceDefectRequirement.DeductPrice => "Уменьшить цену",
        AcceptanceDefectRequirement.Reject => "Не принимать",
        _ => requirement.ToString(),
    };
}

/// <summary>
/// Ставки НДС, которые применяются в документе о приёмке.
///
/// 0 % — экспорт и не облагаемые операции, 5 % и 7 % — упрощённые режимы,
/// 10 % и 20 % — обычные ставки. Произвольное число ставкой не является,
/// поэтому проверка идёт по списку, а не по диапазону.
/// </summary>
public static class AcceptanceVatRates
{
    public static readonly IReadOnlyList<decimal> Allowed = [0m, 5m, 7m, 10m, 20m];

    public static bool IsAllowed(decimal rate) => Allowed.Contains(rate);
}
