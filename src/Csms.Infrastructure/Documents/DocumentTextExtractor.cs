using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Csms.Infrastructure.Npa;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Достаёт из файла чистый текст с переносами строк. Никакой интерпретации
/// смысла: сегментатор и LLM работают уже с этим текстом, поэтому контракт
/// не должен зависеть от формата входного файла.
/// </summary>
public sealed class DocumentTextExtractor(
    DocumentFunctionsOptions options,
    INpaOcrEngine ocr)
{
    public async Task<string> ExtractAsync(
        byte[] content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".txt" or ".md" or ".text")
        {
            return DecodeText(content);
        }

        if (extension == ".docx")
        {
            using var stream = new MemoryStream(content);
            return ExtractDocx(stream);
        }

        if (extension == ".pdf" || HasPdfSignature(content))
        {
            return await ExtractPdfAsync(content, cancellationToken);
        }

        throw new InvalidOperationException(
            "Поддерживаются .docx, .pdf, .txt и .md. Получен: " +
            (string.IsNullOrEmpty(extension) ? "файл без расширения" : extension));
    }

    private static string DecodeText(byte[] content)
    {
        // Файлы загружаются вручную: сначала пробуем UTF-8, затем WINDOWS-1251.
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            return utf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(content);
        }
    }

    private static string ExtractDocx(Stream stream)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document.Body;
        if (body is null)
            return string.Empty;

        // Обход в порядке документа: абзацы внутри таблиц тоже попадают в текст.
        var paragraphs = body.Descendants<Paragraph>()
            .Select(paragraph => paragraph.InnerText.Trim())
            .Where(text => text.Length > 0);
        return string.Join("\n", paragraphs);
    }

    private async Task<string> ExtractPdfAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        using var renderer = PdfDocumentRenderer.Open(pdf, options.Dpi);
        var pages = Math.Min(renderer.PageCount, options.MaxPages);
        var workDirectory = Path.Combine(
            Path.GetTempPath(), "csms-docfunctions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        var result = new StringBuilder();
        try
        {
            for (var index = 0; index < pages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = Path.Combine(workDirectory, $"page-{index + 1:000}.bmp");
                var rendered = renderer.RenderPage(index, bitmap);
                var recognized = await ocr.RecognizeAsync(rendered, cancellationToken);
                result.Append("[стр. ").Append(recognized.PageNumber).AppendLine("]");
                foreach (var line in GroupLines(recognized.Text))
                {
                    result.AppendLine(line);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, true);
            }
            catch (IOException)
            {
                // Временные BMP не влияют на результат — уборка best effort.
            }
        }

        return result.ToString();
    }

    private static IEnumerable<string> GroupLines(IReadOnlyList<OcrWord> words)
    {
        var ordered = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderBy(word => word.CenterY)
            .ThenBy(word => word.Left)
            .ToList();
        if (ordered.Count == 0)
            yield break;

        var heights = ordered.Select(word => word.Height).OrderBy(value => value).ToList();
        var tolerance = Math.Max(6, (int)(heights[heights.Count / 2] * 0.55));
        var lines = new List<List<OcrWord>>();

        foreach (var word in ordered)
        {
            var target = lines
                .Where(line => Math.Abs(line[0].CenterY - word.CenterY) <= tolerance)
                .OrderBy(line => Math.Abs(line[0].CenterY - word.CenterY))
                .FirstOrDefault();
            if (target is null)
            {
                lines.Add([word]);
            }
            else
            {
                target.Add(word);
            }
        }

        foreach (var line in lines.OrderBy(line => line[0].CenterY))
        {
            yield return string.Join(' ', line.OrderBy(word => word.Left).Select(word => word.Text));
        }
    }

    private static bool HasPdfSignature(byte[] content) =>
        content.Length > 4 &&
        content[0] == (byte)'%' &&
        content[1] == (byte)'P' &&
        content[2] == (byte)'D' &&
        content[3] == (byte)'F';
}
