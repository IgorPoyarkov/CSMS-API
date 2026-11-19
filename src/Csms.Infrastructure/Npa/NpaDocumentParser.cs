using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Npa;

/// <summary>Разобранный акт: строки таблицы, оговорки и то, что не разобралось.</summary>
public sealed record NpaParsedDocument(
    IReadOnlyList<NpaTableRow> Rows,
    IReadOnlyList<NpaExclusion> Exclusions,
    IReadOnlyList<NpaUnresolvedRow> Unresolved,
    int PageCount);

/// <summary>
/// Разбор PDF со сканами: страница → растр → две проекции OCR → строки таблицы.
/// Страницы обрабатываются по одной, временный BMP сразу удаляется: у акта на
/// 145 страниц иначе набралось бы 4 ГБ временных файлов.
/// </summary>
public sealed class NpaPdfParser(
    NpaOptions options,
    INpaOcrEngine ocr,
    NpaLayoutParser layout,
    ILogger<NpaPdfParser> logger)
{
    public async Task<NpaParsedDocument> ParseAsync(
        byte[] pdf,
        NpaDocumentOptions document,
        Okpd2CodeResolver resolver,
        CancellationToken cancellationToken = default)
    {
        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "csms-npa",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        var rows = new List<NpaTableRow>();
        var exclusions = new List<NpaExclusion>();
        var unresolved = new List<NpaUnresolvedRow>();

        try
        {
            using var renderer = PdfDocumentRenderer.Open(pdf, options.Ocr.Dpi);
            var pages = Math.Min(renderer.PageCount, options.Ocr.MaxPages);

            logger.LogInformation(
                "Разбор {Code}: {Pages} стр., {Dpi} dpi, движок {Engine}",
                document.Code,
                pages,
                options.Ocr.Dpi,
                ocr.Name);

            for (var index = 0; index < pages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bitmap = Path.Combine(workDirectory, $"page-{index + 1:000}.bmp");
                var rendered = renderer.RenderPage(index, bitmap);

                var page = await ocr.RecognizeAsync(rendered, cancellationToken);
                var result = layout.Parse(page, document, resolver);

                rows.AddRange(result.Rows);
                exclusions.AddRange(result.Exclusions);
                unresolved.AddRange(result.Unresolved);

                File.Delete(bitmap);

                logger.LogDebug(
                    "Стр. {Page}: кодов {Codes}, оговорок {Exclusions}, неразобранных {Unresolved}",
                    result.Page,
                    result.Rows.Count,
                    result.Exclusions.Count,
                    result.Unresolved.Count);
            }

            return new NpaParsedDocument(rows, exclusions, unresolved, pages);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Каталог в TEMP: его уберёт система.
        }
    }
}

/// <summary>
/// Разбор .docx с таблицей перечня. В Word сохраняется структура ячеек, ради
/// которой всё и делается: код и наименование берутся из одной строки, а
/// сноски подтягиваются из FootnotesPart.
/// </summary>
public static class NpaDocxParser
{
    public static NpaParsedDocument Parse(Stream stream, Okpd2CodeResolver resolver)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document.Body;
        if (body is null)
        {
            return new NpaParsedDocument([], [], [], 0);
        }

        var footnotes = ReadFootnotes(document);

        var rows = new List<NpaTableRow>();
        var exclusions = new List<NpaExclusion>();
        var unresolved = new List<NpaUnresolvedRow>();

        foreach (var table in body.Descendants<Table>())
        {
            var rowIndex = 0;
            foreach (var tableRow in table.Descendants<TableRow>())
            {
                var cells = tableRow.Descendants<TableCell>()
                    .Select(cell => cell.InnerText.Trim())
                    .Where(text => text.Length > 0)
                    .ToArray();

                if (cells.Length == 0)
                {
                    continue;
                }

                var joined = string.Join(" | ", cells);
                var notes = Footnotes(tableRow, footnotes);

                // Заголовок таблицы: «Код по ОКПД2», «Наименование».
                if (joined.Contains("Код по", StringComparison.OrdinalIgnoreCase) &&
                    joined.Contains("Наименование", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolution = cells
                    .Select(cell => resolver.Resolve(cell))
                    .FirstOrDefault(item => item is not null);

                if (resolution is not null)
                {
                    rowIndex++;
                    var name = string.Join(" ", cells.Where(cell => cell != resolution.Code));
                    var quote = notes.Length > 0 ? $"{name} {notes}" : name;

                    rows.Add(new NpaTableRow(
                        1,
                        rowIndex,
                        resolution.Code,
                        name,
                        quote,
                        resolution.Confidence,
                        "docx"));
                }
                else if (IsExclusion(joined))
                {
                    foreach (var token in joined.Split([' ', ',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var code = resolver.Resolve(token);
                        if (code is not null &&
                            !exclusions.Any(item => item.Code == code.Code))
                        {
                            exclusions.Add(new NpaExclusion(1, code.Code, joined));
                        }
                    }
                }
                else
                {
                    unresolved.Add(new NpaUnresolvedRow(1, ++rowIndex, joined, "нет кода ОКПД2 в строке"));
                }
            }
        }

        // Текст вне таблиц: в актах вида «оценка заявок» коды встречаются и в абзацах.
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (paragraph.Ancestors<Table>().Any())
            {
                continue;
            }

            var text = paragraph.InnerText.Trim();
            if (text.Length < 20)
            {
                continue;
            }

            if (!IsExclusion(text))
            {
                continue;
            }

            foreach (var token in text.Split([' ', ',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            {
                var code = resolver.Resolve(token);
                if (code is not null && !exclusions.Any(item => item.Code == code.Code))
                {
                    exclusions.Add(new NpaExclusion(1, code.Code, text));
                }
            }
        }

        return new NpaParsedDocument(rows, exclusions, unresolved, 1);
    }

    private static bool IsExclusion(string text) =>
        text.Contains("кроме", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("за исключением", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("исключ", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ReadFootnotes(WordprocessingDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var part = document.MainDocumentPart?.FootnotesPart;
        if (part?.Footnotes is null)
        {
            return result;
        }

        foreach (var footnote in part.Footnotes.Elements<Footnote>())
        {
            if (footnote.Id is null || footnote.Id.Value <= 0)
            {
                continue;
            }

            result[footnote.Id.Value.ToString()] = footnote.InnerText.Trim();
        }

        return result;
    }

    private static string Footnotes(TableRow row, Dictionary<string, string> footnotes)
    {
        var parts = new List<string>();

        foreach (var reference in row.Descendants<FootnoteReference>())
        {
            var id = reference.Id?.Value.ToString();
            if (id is not null && footnotes.TryGetValue(id, out var text))
            {
                parts.Add($"[сноска {id}] {text}");
            }
        }

        return string.Join(" ", parts);
    }
}
