using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Csms.Tests;

/// <summary>
/// Минимальный XLSX для тестов парсера: общие строки и первый лист. Этого
/// достаточно, чтобы проверить разбор колонок B–J без внешних библиотек.
/// </summary>
internal static class EisCounterpartyXlsxFixture
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static readonly string[] Headers =
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

    public static byte[] Create(params Row[] rows) => Build(Headers, rows);

    /// <summary>Вариант с произвольными заголовками — для проверки валидации.</summary>
    public static byte[] CreateWithHeaders(string[] headers, params Row[] rows) =>
        Build(headers, rows);

    private static byte[] Build(string[] headers, params Row[] rows)
    {
        var strings = new List<string>();
        foreach (var header in headers)
        {
            AddShared(strings, header);
        }

        foreach (var row in rows)
        {
            AddShared(strings, row.Inn);
            AddShared(strings, row.Ogrn);
            AddShared(strings, row.Kpp);
            AddShared(strings, row.Name);
            AddShared(strings, row.ParticipantType);
            AddShared(strings, row.Court);
            AddShared(strings, row.CaseNumber);
            AddShared(strings, row.DecisionDate);
            AddShared(strings, row.EffectiveDate);
        }

        var sharedXml = new StringBuilder();
        sharedXml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sharedXml.Append("<sst xmlns=\"").Append(Main).Append("\">");
        foreach (var value in strings)
        {
            sharedXml.Append("<si><t xml:space=\"preserve\">")
                .Append(SecurityElement.Escape(value))
                .Append("</t></si>");
        }

        sharedXml.Append("</sst>");

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sheet.Append("<worksheet xmlns=\"").Append(Main).Append("\"><sheetData>");
        sheet.Append("<row r=\"1\">");
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Append("<c r=\"").Append((char)('B' + index)).Append("1\" t=\"s\"><v>")
                .Append(IndexOf(strings, headers[index]))
                .Append("</v></c>");
        }

        sheet.Append("</row>");

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var number = index + 2;

            sheet.Append("<row r=\"").Append(number).Append("\"><c r=\"A")
                .Append(number).Append("\"><v>").Append(index).Append("</v></c>");

            Text(sheet, 'B', number, strings, row.Inn);
            Text(sheet, 'C', number, strings, row.Ogrn);
            Text(sheet, 'D', number, strings, row.Kpp);
            Text(sheet, 'E', number, strings, row.Name);
            Text(sheet, 'F', number, strings, row.ParticipantType);
            Text(sheet, 'G', number, strings, row.Court);
            Text(sheet, 'H', number, strings, row.CaseNumber);
            Date(sheet, 'I', number, strings, row.DecisionDate);
            Date(sheet, 'J', number, strings, row.EffectiveDate);

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "xl/sharedStrings.xml", sharedXml.ToString());
            Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static void Text(
        StringBuilder sheet,
        char column,
        int rowNumber,
        List<string> strings,
        string value)
    {
        sheet.Append("<c r=\"").Append(column).Append(rowNumber)
            .Append("\" t=\"s\"><v>").Append(IndexOf(strings, value))
            .Append("</v></c>");
    }

    private static void Date(
        StringBuilder sheet,
        char column,
        int rowNumber,
        List<string> strings,
        string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            sheet.Append("<c r=\"").Append(column).Append(rowNumber).Append("\"><v>")
                .Append(value).Append("</v></c>");

            return;
        }

        Text(sheet, column, rowNumber, strings, value);
    }

    private static int AddShared(List<string> strings, string value)
    {
        var index = IndexOf(strings, value);
        if (index >= 0)
        {
            return index;
        }

        strings.Add(value);

        return strings.Count - 1;
    }

    private static int IndexOf(List<string> strings, string value)
    {
        for (var index = 0; index < strings.Count; index++)
        {
            if (string.Equals(strings[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public readonly record struct Row(
        string Inn,
        string Ogrn,
        string Kpp,
        string Name,
        string ParticipantType,
        string Court,
        string CaseNumber,
        string DecisionDate,
        string EffectiveDate);
}
