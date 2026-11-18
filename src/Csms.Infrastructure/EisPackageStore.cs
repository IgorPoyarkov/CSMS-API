using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Строки таблиц <c>eis.packages</c> и <c>eis.package_events</c>.
///
/// Это не доменные объекты, а их отражение в базе: у <see cref="EisPackage"/>
/// намеренно нет публичных сеттеров, поэтому EF не может и не должен строить
/// его сам. Репозиторий переводит домен в эти строки и обратно, а правила
/// переходов остаются в домене.
/// </summary>
public sealed class EisPackageRow
{
    public Guid Id { get; set; }

    /// <summary>Тип сведения строкой: «SmpAttraction», «ContractExecution».</summary>
    public string DocumentType { get; set; } = string.Empty;

    public string ContractEisNumber { get; set; } = string.Empty;

    public string Ikz { get; set; } = string.Empty;

    public string PayloadXml { get; set; } = string.Empty;

    public string PayloadSchemaVersion { get; set; } = string.Empty;

    public string? SignatureBase64 { get; set; }

    public string? SignerThumbprint { get; set; }

    public DateTimeOffset? SignedAt { get; set; }

    public string? EisGuid { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Токен оптимистичной блокировки: <c>xmin</c> из строки PostgreSQL. В домен
    /// он не попадает — это забота хранилища.
    /// </summary>
    public uint Xmin { get; set; }

    /// <summary>Ошибки ФЛК целиком, как их вернула ЕИС (колонка jsonb).</summary>
    public List<FlkError> FlkErrors { get; set; } = [];
}

/// <summary>
/// Запись журнала переходов. Таблица только на дозапись: миграция запрещает
/// UPDATE и DELETE, поэтому «подправить историю задним числом» не выйдет
/// даже при ошибке в коде.
/// </summary>
public sealed class EisPackageEventRow
{
    public Guid Id { get; set; }

    public Guid PackageId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Порядковый номер внутри пакета: нужен, чтобы восстановить порядок при равных метках времени.</summary>
    public int OrderIndex { get; set; }

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;
}
