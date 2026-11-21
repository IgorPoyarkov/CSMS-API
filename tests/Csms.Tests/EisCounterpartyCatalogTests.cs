using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

public sealed class EisCounterpartyCatalogTests
{
    private static readonly CounterpartyScreeningSource Source = new(
        "44571", "20 ноября 2026", "ABCDEF", 2, DateTimeOffset.UtcNow);

    private static CounterpartyScreeningEntry Entry(
        string inn = "1650098034",
        string ogrn = "1031616000503",
        string kpp = "166101001",
        string caseNumber = "5-2-224/2026",
        DateOnly? decisionDate = null) => new(
        inn,
        ogrn,
        kpp,
        "ООО «Пример»",
        "Юридическое лицо РФ",
        "Суд",
        caseNumber,
        decisionDate ?? new DateOnly(2026, 2, 27),
        new DateOnly(2026, 9, 14));

    [Fact]
    public void Поиск_по_ИНН_ОГРН_и_КПП_возвращает_реквизиты_источника()
    {
        var catalog = new EisCounterpartyCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        catalog.Replace(Source,
        [
            Entry(),
            Entry(
                inn: "0100002636",
                ogrn: "1220100002110",
                kpp: "010001001"),
        ]);

        var byInn = catalog.Check(" 1650098034 ", null, null);
        var byOgrn = catalog.Check(null, "1031616000503", null);
        var byKppOnly = catalog.Check(null, null, "010001001");
        var withStrongAndKpp = catalog.Check("1650098034", "1031616000503", "010001001");

        Assert.True(byInn.IsListed);
        Assert.Single(byInn.Matches);
        Assert.Equal("44571", byInn.Source?.SourceId);
        Assert.Equal("ABCDEF", byInn.Source?.Sha256);
        Assert.Equal("20 ноября 2026", byInn.Source?.PublishedDate);
        Assert.True(byOgrn.IsListed);
        Assert.True(byKppOnly.IsListed);
        Assert.Single(byKppOnly.Matches);
        Assert.Equal("0100002636", byKppOnly.Matches[0].Inn);
        // При сильном реквизите общий КПП другой записи не должен совпадать.
        Assert.Single(withStrongAndKpp.Matches);
        Assert.Equal("1650098034", withStrongAndKpp.Matches[0].Inn);
    }

    [Fact]
    public void Неизвестный_реквизит_не_даёт_совпадений()
    {
        var catalog = new EisCounterpartyCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        catalog.Replace(Source, [Entry()]);

        var result = catalog.Check("7700000000", "7700000000000", "770101001");

        Assert.False(result.IsListed);
        Assert.Empty(result.Matches);
        Assert.Equal("44571", result.Source?.SourceId);
    }

    [Fact]
    public void Повторные_строки_по_ключу_показываются_один_раз()
    {
        var catalog = new EisCounterpartyCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var duplicate = Entry(kpp: "166101002");
        catalog.Replace(Source, [Entry(), duplicate]);

        var result = catalog.Check("1650098034", null, null);

        Assert.Single(result.Matches);
    }

    [Fact]
    public void Пустой_каталог_возвращает_отрицательный_результат()
    {
        var catalog = new EisCounterpartyCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        var result = catalog.Check("1650098034", null, null);

        Assert.False(result.IsListed);
        Assert.Null(result.Source);
    }
}
