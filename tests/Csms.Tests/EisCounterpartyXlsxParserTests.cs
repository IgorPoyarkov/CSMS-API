using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Парсер выгрузки ст. 19.28 КоАП проверяем на синтетическом XLSX и, при
/// наличии переменной EIS_COUNTERPARTIES_XLSX, на настоящем файле ЕИС.
/// </summary>
public sealed class EisCounterpartyXlsxParserTests
{
    private static EisCounterpartyXlsxFixture.Row SampleRow(
        string inn = " 1650098034",
        string decision = "46080",
        string effective = "46279") => new(
        inn,
        "1031616000503",
        "166101001",
        "ООО «Пример»",
        "Юридическое лицо РФ",
        "Мировой судья судебного участка № 2",
        "5-2-224/2026",
        decision,
        effective);

    [Fact]
    public void Разбираются_колонки_Б_Й_и_ведущие_нули_ИНН()
    {
        var bytes = EisCounterpartyXlsxFixture.Create(
            SampleRow(),
            SampleRow(inn: "0100002636", decision: "45827", effective: "45838"));

        var entries = EisCounterpartyXlsxParser.Parse(bytes);

        Assert.Equal(2, entries.Count);
        Assert.Equal("1650098034", entries[0].Inn);
        Assert.Equal("0100002636", entries[1].Inn);
        Assert.Equal("1031616000503", entries[0].Ogrn);
        Assert.Equal("166101001", entries[0].Kpp);
        Assert.Equal("ООО «Пример»", entries[0].Name);
        Assert.Equal("Юридическое лицо РФ", entries[0].ParticipantType);
        Assert.Equal("Мировой судья судебного участка № 2", entries[0].Court);
        Assert.Equal("5-2-224/2026", entries[0].CaseNumber);
        Assert.Equal(new DateOnly(2026, 2, 27), entries[0].DecisionDate);
        Assert.Equal(new DateOnly(2026, 9, 14), entries[0].EffectiveDate);
    }

    [Fact]
    public void Текстовая_дата_с_опечаткой_года_приводится_к_современному_году()
    {
        var bytes = EisCounterpartyXlsxFixture.Create(
            SampleRow(decision: "12.05.0202", effective: "11.11.0205"));

        var entries = EisCounterpartyXlsxParser.Parse(bytes);

        Assert.Equal(new DateOnly(2022, 5, 12), entries[0].DecisionDate);
        Assert.Equal(new DateOnly(2025, 11, 11), entries[0].EffectiveDate);
    }

    [Fact]
    public void Иностранное_юридическое_лицо_без_ОГРН_и_КПП_разбирается()
    {
        var bytes = EisCounterpartyXlsxFixture.Create(
            new EisCounterpartyXlsxFixture.Row(
                "9900123456",
                string.Empty,
                string.Empty,
                "Foreign LLC",
                "Юридическое лицо иностранного государства",
                "Суд",
                "A-1/2026",
                "46080",
                "46279"));

        var entries = EisCounterpartyXlsxParser.Parse(bytes);

        Assert.Single(entries);
        Assert.Equal("9900123456", entries[0].Inn);
        Assert.Equal(string.Empty, entries[0].Ogrn);
        Assert.Equal(string.Empty, entries[0].Kpp);
    }

    [Fact]
    public void Невалидная_дата_прерывает_импорт_с_русским_сообщением()
    {
        var bytes = EisCounterpartyXlsxFixture.Create(SampleRow(decision: "не дата"));

        var error = Assert.Throws<InvalidOperationException>(
            () => EisCounterpartyXlsxParser.Parse(bytes));

        Assert.Contains("Строка 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("дату", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Отсутствие_обязательной_колонки_прерывает_импорт()
    {
        var headers = EisCounterpartyXlsxFixture.Headers
            .Take(EisCounterpartyXlsxFixture.Headers.Length - 1)
            .ToArray();
        var bytes = EisCounterpartyXlsxFixture.CreateWithHeaders(headers, SampleRow());

        var error = Assert.Throws<InvalidOperationException>(
            () => EisCounterpartyXlsxParser.Parse(bytes));

        Assert.Contains("B–J", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Пустая_обязательная_ячейка_прерывает_импорт()
    {
        var bytes = EisCounterpartyXlsxFixture.Create(SampleRow(inn: "   "));

        var error = Assert.Throws<InvalidOperationException>(
            () => EisCounterpartyXlsxParser.Parse(bytes));

        Assert.Contains("колонка B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Не_ZIP_файл_прерывает_импорт()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EisCounterpartyXlsxParser.Parse("<html>ошибка</html>"u8.ToArray()));

        Assert.Contains("не XLSX", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Проверка на настоящем файле ЕИС без сети: путь даётся переменной
    /// EIS_COUNTERPARTIES_XLSX. Без неё тест ничего не требует.
    /// </summary>
    [Fact]
    public void Настоящая_выгрузка_ЕИС_разбирается_полностью()
    {
        var path = Environment.GetEnvironmentVariable("EIS_COUNTERPARTIES_XLSX");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var entries = EisCounterpartyXlsxParser.Parse(File.ReadAllBytes(path));

        Assert.True(entries.Count > 900, entries.Count.ToString());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Inn));
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            // У иностранных юридических лиц ОГРН и КПП в выгрузке пустые.
        });
    }
}
