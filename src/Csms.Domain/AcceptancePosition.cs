namespace Csms.Domain;

/// <summary>
/// Позиция документа о приёмке: что именно принято, сколько и по какой цене.
///
/// Цена указывается с НДС — так её видит поставщик в счёте и так её проверяет
/// заказчик. НДС и стоимость без налога считаются из неё, а не вводятся
/// отдельно: иначе сумма позиции и сумма документа расходятся.
/// </summary>
public sealed class AcceptancePosition
{
    private AcceptancePosition(
        Guid id,
        string name,
        string? okpd2,
        string unit,
        decimal quantity,
        decimal unitPrice,
        decimal vatRate,
        string? countryOrigin,
        string? registryNumber)
    {
        Id = id;
        Name = name;
        Okpd2 = okpd2;
        Unit = unit;
        Quantity = quantity;
        UnitPrice = unitPrice;
        VatRate = vatRate;
        CountryOrigin = countryOrigin;
        RegistryNumber = registryNumber;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string? Okpd2 { get; }

    public string Unit { get; }

    public decimal Quantity { get; }

    /// <summary>Цена за единицу с НДС.</summary>
    public decimal UnitPrice { get; }

    public decimal VatRate { get; }

    public string? CountryOrigin { get; }

    /// <summary>Номер реестровой записи российского товара, если применимо.</summary>
    public string? RegistryNumber { get; }

    /// <summary>Стоимость позиции с НДС.</summary>
    public decimal TotalWithVat => Round(Quantity * UnitPrice);

    /// <summary>НДС в составе стоимости: сумма × ставка / (100 + ставка).</summary>
    public decimal VatAmount => Round(TotalWithVat * VatRate / (100m + VatRate));

    public decimal TotalWithoutVat => TotalWithVat - VatAmount;

    public static AcceptancePosition Create(
        string name,
        string? okpd2,
        string unit,
        decimal quantity,
        decimal unitPrice,
        decimal vatRate,
        string? countryOrigin,
        string? registryNumber)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Не указано наименование товара, работы или услуги в позиции.");
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            throw new DomainException("Не указана единица измерения позиции.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("Количество в позиции должно быть больше нуля.");
        }

        if (unitPrice <= 0)
        {
            throw new DomainException("Цена за единицу в позиции должна быть больше нуля.");
        }

        if (!AcceptanceVatRates.IsAllowed(vatRate))
        {
            throw new DomainException(
                $"Ставка НДС {vatRate:0.##} % не применяется: допустимы " +
                string.Join(", ", AcceptanceVatRates.Allowed.Select(rate => $"{rate:0.##} %")) + ".");
        }

        return new AcceptancePosition(
            Guid.NewGuid(),
            name.Trim(),
            Normalize(okpd2),
            unit.Trim(),
            quantity,
            unitPrice,
            vatRate,
            Normalize(countryOrigin),
            Normalize(registryNumber));
    }

    /// <summary>Восстановление из хранилища: значения уже проверены при создании.</summary>
    public static AcceptancePosition Restore(
        Guid id,
        string name,
        string? okpd2,
        string unit,
        decimal quantity,
        decimal unitPrice,
        decimal vatRate,
        string? countryOrigin,
        string? registryNumber) =>
        new(id, name, okpd2, unit, quantity, unitPrice, vatRate, countryOrigin, registryNumber);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Выявленный при приёмке недостаток: что не так и что с этим делать.
///
/// Ссылка на позицию необязательна: недостаток может касаться документа
/// в целом (например, не представлена исполнительная документация).
/// </summary>
public sealed record AcceptanceDefect(
    Guid Id,
    Guid? PositionId,
    string Description,
    decimal Quantity,
    AcceptanceDefectRequirement Requirement)
{
    public static AcceptanceDefect Create(
        Guid? positionId,
        string description,
        decimal quantity,
        AcceptanceDefectRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 10)
        {
            throw new DomainException("Опишите недостаток (не менее 10 символов): без описания требование не проверить.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("Количество по недостатку должно быть больше нуля.");
        }

        return new AcceptanceDefect(
            Guid.NewGuid(),
            positionId,
            description.Trim(),
            quantity,
            requirement);
    }

    public static AcceptanceDefect Restore(
        Guid id,
        Guid? positionId,
        string description,
        decimal quantity,
        AcceptanceDefectRequirement requirement) =>
        new(id, positionId, description, quantity, requirement);
}

/// <summary>Событие аудита по документу о приёмке.</summary>
public sealed record AcceptanceEvent(
    DateTimeOffset At,
    AcceptanceStatus From,
    AcceptanceStatus To,
    string Action,
    string Comment,
    string Actor);
