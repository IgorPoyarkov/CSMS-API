using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Разбор публичной выгрузки ЕИС по ст. 19.28 КоАП без внешних пакетов:
/// XLSX — это ZIP, а содержимое листа — XML. Берём только колонки B–J;
/// аналоги ИНН и ведущие нули сохраняем строками, даты Excel переводим
/// через <see cref="DateTime.FromOADate(double)"/>.
/// </summary>
public static class EisCounterpartyXlsxParser
{
    private const string SharedStringsPath = "xl/sharedStrings.xml";
    private const string SheetPath = "xl/worksheets/sheet1.xml";

    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly Regex CellColumn = new(
        "^(?<column>[A-Z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant);

    // ЕИС иногда складывает дату текстом «дд.мм.гггг», причём год может быть
    // напечатан как 0202/0205. Ниже это приводится к 2022/2025: цифры года
    // берём из самого файла, ничего не выдумываем.
    private static readonly Regex TextDate = new(
        @"^(?<day>\d{1,2})\.(?<month>\d{1,2})\.(?<year>\d{4})$",
        RegexOptions.CultureInvariant);

    // Заголовок проверяется целиком: подмена файла другой выгрузкой ЕИС
    // должна прервать импорт, а не молча создать пустой каталог.
    private static readonly string[] RequiredHeaders =
    [
        "ИНН/Аналог ИНН",
        "ОГРН",
        "КПП",
        "Наименование ЮЛ",
        "Тип участника",
        "Суд",
        "Номер дела",
        "Дата вынесения постановления",
        "Дата вступления в законную силу постановления о назначении административного наказания",
    ];

    /// <summary>Прочитать все строки payload-листа выгрузки.</summary>
    public static IReadOnlyList<CounterpartyScreeningEntry> Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 2 || bytes[0] != 0x50 || bytes[1] != 0x4B)
        {
            throw new InvalidOperationException(
                "ЕИС вернула не XLSX-файл: содержимое не является ZIP-архивом.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var stringsEntry = archive.GetEntry(SharedStringsPath)
            ?? throw new InvalidOperationException(
                $"В выгрузке ЕИС отсутствует {SharedStringsPath} — файл повреждён.");
        var sheetEntry = archive.GetEntry(SheetPath)
            ?? throw new InvalidOperationException(
                $"В выгрузке ЕИС отсутствует {SheetPath} — файл повреждён.");

        IReadOnlyList<string> strings;
        using (var sharedStream = stringsEntry.Open())
        {
            strings = ReadSharedStrings(sharedStream);
        }

        XDocument sheet;
        using (var sheetStream = sheetEntry.Open())
        {
            sheet = ReadXml(sheetStream);
        }

        var rows = (sheet.Root?.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
            .ToList();

        var headerIndex = rows.FindIndex(row => IsHeader(row, strings));
        if (headerIndex < 0)
        {
            throw new InvalidOperationException(
                "В выгрузке ЕИС не найдены обязательные колонки B–J " +
                "(ИНН/Аналог ИНН, ОГРН, КПП, Наименование ЮЛ, Тип участника, Суд, " +
                "Номер дела и две даты) — импорт остановлен.");
        }

        var result = new List<CounterpartyScreeningEntry>();

        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var cells = CellsByColumn(row);
            if (cells.Count == 0)
            {
                continue;
            }

            var rowNumber = (string?)row.Attribute("r") ?? "?";

            string Required(char column, string title)
            {
                if (!cells.TryGetValue(column.ToString(), out var cell))
                {
                    throw new InvalidOperationException(
                        $"Строка {rowNumber}: отсутствует колонка {column} «{title}».");
                }

                var value = CellText(cell, strings).Trim();
                if (value.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Строка {rowNumber}: колонка {column} «{title}» пуста.");
                }

                return value;
            }

            string Optional(char column, string title)
            {
                if (!cells.TryGetValue(column.ToString(), out var cell))
                {
                    return string.Empty;
                }

                return CellText(cell, strings).Trim();
            }

            var inn = Required('B', RequiredHeaders[0]);

            // У иностранных юридических лиц в выгрузке нет ОГРН и КПП — это
            // нормальная ситуация, такой контрагент проверяется по аналогу ИНН.
            var ogrn = Optional('C', RequiredHeaders[1]);
            var kpp = Optional('D', RequiredHeaders[2]);

            var name = Required('E', RequiredHeaders[3]);
            var participantType = Required('F', RequiredHeaders[4]);
            var court = Required('G', RequiredHeaders[5]);
            var caseNumber = Required('H', RequiredHeaders[6]);

            var decisionDate = ParseExcelDate(
                Required('I', RequiredHeaders[7]), rowNumber, 'I', RequiredHeaders[7]);
            var effectiveDate = ParseExcelDate(
                Required('J', RequiredHeaders[8]), rowNumber, 'J', RequiredHeaders[8]);

            result.Add(new CounterpartyScreeningEntry(
                inn, ogrn, kpp, name, participantType, court, caseNumber,
                decisionDate, effectiveDate));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadSharedStrings(Stream stream)
    {
        var document = ReadXml(stream);

        return (document.Root?.Elements(Main + "si") ?? [])
            .Select(item => string.Concat(item.Descendants(Main + "t").Select(node => node.Value)))
            .ToList();
    }

    private static XDocument ReadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(stream, settings);

        return XDocument.Load(reader);
    }

    private static bool IsHeader(XElement row, IReadOnlyList<string> strings)
    {
        var cells = CellsByColumn(row);

        for (var index = 0; index < RequiredHeaders.Length; index++)
        {
            var column = (char)('B' + index);
            if (!cells.TryGetValue(column.ToString(), out var cell) ||
                !string.Equals(
                    NormalizeHeader(CellText(cell, strings)),
                    RequiredHeaders[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, XElement> CellsByColumn(XElement row)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in row.Elements(Main + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            var match = reference is null ? null : CellColumn.Match(reference);
            if (match is { Success: true })
            {
                result[match.Groups["column"].Value] = cell;
            }
        }

        return result;
    }

    private static string CellText(XElement cell, IReadOnlyList<string> strings)
    {
        var type = (string?)cell.Attribute("t");

        if (string.Equals(type, "s", StringComparison.Ordinal))
        {
            var raw = cell.Element(Main + "v")?.Value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index >= strings.Count)
            {
                throw new InvalidOperationException(
                    $"В выгрузке ЕИС некорректная ссылка на общую строку: «{raw}».");
            }

            return strings[index];
        }

        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            var inline = cell.Element(Main + "is");

            return inline is null
                ? string.Empty
                : string.Concat(inline.Descendants(Main + "t").Select(node => node.Value));
        }

        return cell.Element(Main + "v")?.Value ?? string.Empty;
    }

    private static DateOnly ParseExcelDate(
        string value,
        string rowNumber,
        char column,
        string title)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                var date = DateTime.FromOADate(serial);
                if (date.Year is >= 1900 and <= 2200)
                {
                    return DateOnly.FromDateTime(date);
                }
            }
            catch (ArgumentException)
            {
                // Ниже единое сообщение для человека — без системного текста исключения.
            }
        }

        var text = TextDate.Match(value.Trim());
        if (text.Success)
        {
            var day = int.Parse(text.Groups["day"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(text.Groups["month"].Value, CultureInfo.InvariantCulture);
            var yearText = text.Groups["year"].Value;

            // В актуальной выгрузке встречаются «12.05.0202» и «11.11.0205»:
            // это опечатка года, а не II век. Заменяем ведущий ноль на «20»
            // и сохраняем две значащие цифры — получаем 2022/2025.
            if (yearText.Length == 4 && yearText[0] == '0')
            {
                yearText = "20" + yearText[1] + yearText[3];
            }

            var year = int.Parse(yearText, CultureInfo.InvariantCulture);
            if (year is < 1900 or > 2200)
            {
                throw new ArgumentOutOfRangeException(nameof(year));
            }

            try
            {
                return new DateOnly(year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Сообщение ниже.
            }
        }

        throw new InvalidOperationException(
            $"Строка {rowNumber}: не удалось разобрать дату «{value}» " +
            $"в колонке {column} «{title}» — ожидается дата Excel или дд.мм.гггг.");
    }

    // Универсальная нормализация пробелов пригодится, если ЕИС добавит
    // переносы строк в длинный заголовок колонки J.
    private static string NormalizeHeader(string value) => WhiteSpace.Replace(value.Trim(), " ");
}
