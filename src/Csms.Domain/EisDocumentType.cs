namespace Csms.Domain;

/// <summary>Тип сведения, которое уходит в ЕИС.</summary>
public enum EisDocumentType
{
    /// <summary>Сведения о привлечении СМП к исполнению контракта.</summary>
    SmpAttraction = 0,

    /// <summary>Сведения об исполнении контракта.</summary>
    ContractExecution = 1,
}
