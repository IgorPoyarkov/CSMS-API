namespace Csms.Domain;

/// <summary>
/// Итог контроля объёма привлечения: требуемый рублёвый объём, факт, недобор,
/// процент закрытия и признак того, что obligation выполнен.
/// </summary>
public sealed record SmpVolumeResult(
    decimal Required,
    decimal Actual,
    decimal Shortfall,
    decimal Progress,
    bool IsComplete);

/// <summary>Начисленная неустойка за недопривлечение (п. 6 типовых условий).</summary>
public sealed record SmpPenaltyResult(
    decimal Amount,
    decimal Shortfall,
    int OverdueDays,
    string Description,
    string Reference);

/// <summary>
/// Расчёты по объёму attracting и неустойке. Перенесены с фронта в домен, чтобы
/// карточка контракта и API считали одинаково: в объём идут только принятые
/// договоры с субподрядчиками из реестра МСП/СОНКО.
/// </summary>
public static class ContractSmpVolume
{
    /// <summary>Ставка п. 6: 0,1 % от суммы недопривлечения за каждый день.</summary>
    public const decimal DailyPenaltyRate = 0.001m;

    public static bool CountsTowardsVolume(ContractSmpRecord record) =>
        record.Input.DocumentType == ContractSmpDocumentType.SubContract &&
        record.Input.IsSmpSubject &&
        record.Status is ContractSmpStatus.Accepted or ContractSmpStatus.PushedToEis;

    public static SmpVolumeResult Calculate(
        IReadOnlyList<ContractSmpRecord> records,
        decimal contractPrice,
        decimal? requiredPercent)
    {
        var required = requiredPercent is null
            ? 0m
            : Math.Round(contractPrice * requiredPercent.Value / 100m, 2, MidpointRounding.AwayFromZero);

        var actual = records.Where(CountsTowardsVolume)
            .Sum(record => record.Input.Amount ?? 0m);

        var shortfall = Math.Max(0m, required - actual);
        var progress = required <= 0 ? 0m : Math.Round(actual / required * 100m, 2);

        return new SmpVolumeResult(required, actual, shortfall, progress, shortfall <= 0);
    }

    public static SmpPenaltyResult? CalculatePenalty(SmpVolumeResult volume, int overdueDays = 0)
    {
        if (volume.Shortfall <= 0)
        {
            return null;
        }

        var days = overdueDays > 0 ? overdueDays : 1;
        var amount = Math.Round(volume.Shortfall * DailyPenaltyRate * days, 2, MidpointRounding.AwayFromZero);

        return new SmpPenaltyResult(
            amount,
            volume.Shortfall,
            days,
            $"Недопривлечение СМП/СОНКО: недобрано {volume.Shortfall:0.##} руб. " +
            $"({days} раб. дн.). ПП РФ от 23.12.2016 N 1466, п. 6.",
            "ПП РФ от 23.12.2016 N 1466, п. 6");
    }
}

/// <summary>Правило срока по типовым условиям attracting СМП/СОНКО.</summary>
public sealed record SmpDeadlineRule(
    string Code,
    string Clause,
    string Title,
    int WorkingDays,
    string Basis,
    string ActId);

/// <summary>
/// Каталог сроков ПП РФ от 23.12.2016 N 1466 и ч. 3 ст. 103 44-ФЗ.
/// Константы не являются реквизитом контракта: контрактная служба их не
/// заполняет, поэтому они живут в домене, а не в базе данных.
/// </summary>
public static class ContractSmpDeadlineCatalog
{
    public const string SmpActId = "pp-1466";
    public const string CustomerActId = "law-44fz-art103-p3";
    public const int MinimumRequiredPercent = 5;
    public const int EarlyPaymentWorkingDays = 10;
    public const int CustomerEisWorkingDays = 3;

    public static readonly IReadOnlyList<SmpDeadlineRule> Rules =
    [
        new("p2", "п. 2", "Декларация о принадлежности к СМП/СОНКО и заверенная копия договора",
            5, "со дня заключения договора с субподрядчиком", SmpActId),
        new("p3", "п. 3", "Те же документы при замене субподрядчика на этапе исполнения",
            5, "со дня заключения договора с новым субподрядчиком", SmpActId),
        new("p4a", "п. 4", "Копии документов о приёмке выполненных работ (оказанных услуг)",
            10, "со дня оплаты обязательств перед субподрядчиком", SmpActId),
        new("p4b", "п. 4", "Копии платёжных поручений",
            5, "со дня оплаты (10 рабочих дней при оплате раньше срока по контракту)", SmpActId),
        new("p5", "п. 5", "Оплата подрядчиком обязательств перед субподрядчиком",
            7, "с даты подписания подрядчиком документа о приёмке", SmpActId),
    ];

    public static readonly SmpDeadlineRule CustomerEisRule = new(
        "customer",
        "ч. 3 ст. 103 44-ФЗ","Разместить полученные сведения в ЕИС",
        CustomerEisWorkingDays,
        "с момента получения сведений от подрядчика",
        CustomerActId);

    public static SmpDeadlineRule? Find(string code) =>
        Rules.FirstOrDefault(rule => rule.Code == code);
}
