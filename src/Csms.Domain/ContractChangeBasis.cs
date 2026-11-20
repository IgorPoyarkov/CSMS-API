namespace Csms.Domain;

/// <summary>
/// Правовое основание изменения или расторжения контракта.
///
/// Каталог — это не подсказка для интерфейса, а часть правил: от кода
/// основания зависит, какие поля обязательны и какие изменения допустимы.
/// Поэтому проверки не «верят» присланному с фронта набору флагов, а берут
/// их из каталога по коду.
/// </summary>
/// <param name="Code">Устойчивый код для API и хранения.</param>
/// <param name="Kind">Изменение или расторжение.</param>
/// <param name="Article">Норма 44-ФЗ, на которую опирается основание.</param>
/// <param name="Title">Короткое название для интерфейса.</param>
/// <param name="Description">Что именно допускает основание.</param>
public sealed record ContractChangeBasis(
    string Code,
    ContractChangeKind Kind,
    string Article,
    string Title,
    string Description,
    bool AllowsPriceChange = false,
    bool AllowsQuantityChange = false,
    decimal MaxQuantityPercent = 0,
    bool AllowsQualityImprovement = false,
    bool RequiresGovernmentAct = false,
    bool RequiresCourtDecision = false,
    bool RequiresUnilateralNotice = false,
    bool RequiresNewSupplier = false,
    bool RequiresFormula = false,
    bool RequiresUnitPrice = false,
    bool RequiresEisRegistration = true,
    // Изменение цены допускается только в сторону уменьшения
    bool OnlyPriceReduction = false,
    // Максимальное увеличение цены в процентах (0 — увеличение запрещено)
    decimal MaxPriceIncreasePercent = 0);

/// <summary>
/// Основания ст. 95 44-ФЗ, применяемые в модуле.
///
/// Формулировки намеренно не дробятся на пункты там, где редакция менялась:
/// в карточке сохраняется код, часть статьи и текст основания, а конкретный
/// пункт указывается только у бесспорных норм (снижение цены, 10 % объёма).
/// </summary>
public static class ContractChangeBasisCatalog
{
    public static readonly IReadOnlyList<ContractChangeBasis> All =
    [
        // ─── Изменение контракта ────────────────────────────────────────────
        new ContractChangeBasis(
            "price_reduction",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Снижение цены без изменения объёма",
            "Цена контракта снижается без изменения количества товара, объёма работ или услуг и иных существенных условий.",
            AllowsPriceChange: true,
            OnlyPriceReduction: true,
            MaxPriceIncreasePercent: 0),
        new ContractChangeBasis(
            "quantity_10",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Изменение количества (объёма) не более 10 %",
            "Количество товара или объём работ (услуг) меняется не более чем на 10 %; цена может быть пересчитана пропорционально.",
            AllowsPriceChange: true,
            AllowsQuantityChange: true,
            MaxQuantityPercent: 10,
            MaxPriceIncreasePercent: 10),
        new ContractChangeBasis(
            "quality_improvement",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Улучшение характеристик без увеличения цены",
            "Улучшаются качественные, технические или функциональные характеристики предмета контракта без увеличения его цены.",
            AllowsQualityImprovement: true,
            OnlyPriceReduction: true,
            MaxPriceIncreasePercent: 0),
        new ContractChangeBasis(
            "government_act",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Изменение по акту органа власти",
            "Сроки и иные условия изменяются на основании решения Правительства РФ, высшего исполнительного органа субъекта РФ или местной администрации.",
            AllowsPriceChange: true,
            RequiresGovernmentAct: true,
            MaxPriceIncreasePercent: 100),
        new ContractChangeBasis(
            "price_formula",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Изменение цены по формуле",
            "Цена контракта определяется по формуле, и её значение уточняется без изменения объёма и предмета контракта.",
            AllowsPriceChange: true,
            RequiresFormula: true,
            MaxPriceIncreasePercent: 100),
        new ContractChangeBasis(
            "unit_price",
            ContractChangeKind.Modification,
            "ч. 24 ст. 22, ст. 95 44-ФЗ",
            "Изменение цены единицы при неопределённом объёме",
            "При контракте с ценой единицы и неопределённым объёмом меняется цена единицы; она не может превышать начальную максимальную цену.",
            AllowsPriceChange: true,
            RequiresUnitPrice: true,
            MaxPriceIncreasePercent: 100),
        new ContractChangeBasis(
            "supplier_replacement",
            ContractChangeKind.Modification,
            "ч. 17.1 ст. 95 44-ФЗ",
            "Замена поставщика при расторжении",
            "Контракт заключается с единственным поставщиком на условиях расторгнутого контракта в объёме, не превышающем неисполненную часть.",
            RequiresNewSupplier: true,
            MaxPriceIncreasePercent: 0),
        new ContractChangeBasis(
            "bank_support",
            ContractChangeKind.Modification,
            "ч. 1 ст. 95 44-ФЗ",
            "Изменение банковского или казначейского сопровождения",
            "Меняются условия банковского или казначейского сопровождения контракта без изменения предмета и объёма.",
            MaxPriceIncreasePercent: 0),

        // ─── Расторжение контракта ──────────────────────────────────────────
        new ContractChangeBasis(
            "by_agreement",
            ContractChangeKind.Termination,
            "ч. 8 ст. 95 44-ФЗ",
            "Соглашение сторон",
            "Контракт расторгается по соглашению сторон; стороны фиксируют дату прекращения обязательств и расчёты.",
            RequiresEisRegistration: true),
        new ContractChangeBasis(
            "court_decision",
            ContractChangeKind.Termination,
            "ч. 8 ст. 95 44-ФЗ",
            "Решение суда",
            "Контракт расторгается по решению суда; указываются номер, дата и дата вступления решения в силу.",
            RequiresCourtDecision: true,
            RequiresEisRegistration: true),
        new ContractChangeBasis(
            "customer_unilateral",
            ContractChangeKind.Termination,
            "ч. 9–13 ст. 95 44-ФЗ",
            "Односторонний отказ заказчика",
            "Заказчик отказывается от исполнения контракта при существенном нарушении условий; решение вступает в силу через 10 дней после надлежащего уведомления поставщика.",
            RequiresUnilateralNotice: true,
            RequiresEisRegistration: true),
        new ContractChangeBasis(
            "supplier_unilateral",
            ContractChangeKind.Termination,
            "ч. 20–22 ст. 95 44-ФЗ",
            "Односторонний отказ поставщика",
            "Поставщик отказывается от исполнения контракта при существенном нарушении условий заказчиком; уведомление направляется заказчику, решение вступает в силу через 10 дней.",
            RequiresUnilateralNotice: true,
            RequiresEisRegistration: true),
        new ContractChangeBasis(
            "force_majeure",
            ContractChangeKind.Termination,
            "ст. 451 ГК РФ, ч. 8 ст. 95 44-ФЗ",
            "Существенное изменение обстоятельств",
            "Расторжение в связи с существенным изменением обстоятельств, из которых стороны исходили при заключении контракта; требуется судебный акт.",
            RequiresCourtDecision: true,
            RequiresEisRegistration: true),
    ];

    public static ContractChangeBasis? Find(string? code) =>
        All.FirstOrDefault(basis =>
            string.Equals(basis.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ContractChangeBasis> ForKind(ContractChangeKind kind) =>
        All.Where(basis => basis.Kind == kind).ToList();

    /// <summary>Основание, которое домен требует по умолчанию для вида.</summary>
    public static ContractChangeBasis Default(ContractChangeKind kind) =>
        kind == ContractChangeKind.Modification
            ? Find("price_reduction")!
            : Find("by_agreement")!;
}
