using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Csms.Infrastructure.Npa;

/// <summary>Распознанное слово с координатами на странице (пиксели растра).</summary>
public sealed record OcrWord(int Left, int Top, int Width, int Height, string Text, double Confidence)
{
    public int CenterY => Top + (Height / 2);

    public int Right => Left + Width;
}

/// <summary>Страница, снятая с PDF: путь к BMP и размер растра.</summary>
public sealed record RenderedPage(int PageNumber, string BitmapPath, int Width, int Height);

/// <summary>Две проекции одной страницы: обычный текст и только цифры.</summary>
public sealed record OcrPage(
    int PageNumber,
    int Width,
    int Height,
    IReadOnlyList<OcrWord> Text,
    IReadOnlyList<OcrWord> Numeric);

/// <summary>Распознавание страницы. Интерфейс нужен, чтобы тесты шли без Tesseract.</summary>
public interface INpaOcrEngine
{
    /// <summary>Что за движок и язык — попадает в версию документа для аудита.</summary>
    string Name { get; }

    Task<OcrPage> RecognizeAsync(RenderedPage page, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tesseract CLI с русским языком. Две проекции одной страницы:
/// обычная (для наименований и цитат) и с whitelist цифр — в ней коды ОКПД2
/// читаются точнее, потому что модель не путает 0 и О, 1 и l.
/// </summary>
public sealed class TesseractOcrEngine : INpaOcrEngine
{
    private const string NumericWhitelist = "0123456789.";

    private readonly NpaOcrOptions _options;
    private string? _executable;
    private string? _tessdata;
    private string? _version;

    public TesseractOcrEngine(NpaOcrOptions options)
    {
        _options = options;
    }

    public string Name => $"tesseract {_version ?? "?"} ({_options.Language})";

    public async Task<OcrPage> RecognizeAsync(RenderedPage page, CancellationToken cancellationToken = default)
    {
        // Пути проверяются при первом запуске, а не в конструкторе: иначе API
        // не поднимется на машине без Tesseract, хотя остальные функции важнее.
        EnsureResolved();

        var prefix = Path.Combine(
            Path.GetDirectoryName(page.BitmapPath)!,
            $"p{page.PageNumber:000}");

        var text = await RunAsync(page.BitmapPath, prefix + "-text", whitelist: null, cancellationToken);
        var numeric = await RunAsync(page.BitmapPath, prefix + "-num", NumericWhitelist, cancellationToken);

        return new OcrPage(page.PageNumber, page.Width, page.Height, text, numeric);
    }

    private async Task<IReadOnlyList<OcrWord>> RunAsync(
        string bitmap,
        string outputBase,
        string? whitelist,
        CancellationToken cancellationToken)
    {
        var arguments = new StringBuilder();
        arguments.Append('"').Append(bitmap).Append("\" \"").Append(outputBase).Append("\" -l ")
            .Append(_options.Language)
            .Append(" --psm 6 --tessdata-dir \"").Append(_tessdata!).Append('"');

        if (whitelist is not null)
        {
            arguments.Append(" -c tessedit_char_whitelist=").Append(whitelist);
        }

        arguments.Append(" tsv");

        var start = new ProcessStartInfo(_executable!, arguments.ToString())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Не удалось запустить {_executable}.");

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Tesseract завершился с кодом {process.ExitCode}: {stderr.Trim()}");
        }

        var tsv = outputBase + ".tsv";
        if (!File.Exists(tsv))
        {
            throw new InvalidOperationException($"Tesseract не создал {tsv}.");
        }

        var words = ParseTsv(await File.ReadAllLinesAsync(tsv, cancellationToken));
        TryDelete(tsv);
        return words;
    }

    /// <summary>
    /// TSV Tesseract: 11 числовых колонок и текст. Строки не-слов (уровни
    /// страницы/блока/абзаца) отбрасываются, как и пустой текст.
    /// </summary>
    internal static IReadOnlyList<OcrWord> ParseTsv(IReadOnlyList<string> lines)
    {
        var words = new List<OcrWord>(lines.Count);

        for (var index = 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var cells = line.Split('\t');
            if (cells.Length < 12)
            {
                continue;
            }

            var text = string.Join("\t", cells.Skip(11)).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(cells[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
                !int.TryParse(cells[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
                !int.TryParse(cells[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(cells[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            _ = double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence);
            words.Add(new OcrWord(left, top, width, height, text, confidence));
        }

        return words;
    }

    private void EnsureResolved()
    {
        if (_executable is not null && _tessdata is not null)
        {
            return;
        }

        _executable ??= ResolveExecutable(_options.TesseractPath);
        _tessdata ??= ResolveTessdata(_options.TessdataPath);
        _version ??= ReadVersion();
    }

    /// <summary>Версия движка попадает в версию документа: по ней видно, чем распознан текст.</summary>
    private string ReadVersion()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                _executable!,
                "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return "unknown";
            }

            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first?.Replace("tesseract ", string.Empty, StringComparison.OrdinalIgnoreCase)
                       .Trim() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ResolveExecutable(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe"));
        candidates.Add(Path.Combine(programFilesX86, "Tesseract-OCR", "tesseract.exe"));
        candidates.Add("/usr/bin/tesseract");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Tesseract не найден. Установите его (winget install UB-Mannheim.TesseractOCR) " +
            "или задайте Npa:Ocr:TesseractPath.");
    }

    private string ResolveTessdata(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        // Рядом с приложением: ocr/tessdata копируется при сборке.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ocr", "tessdata"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "ocr", "tessdata"));

        // Запуск из bin: поднимаемся к корню репозитория, где лежит ocr/tessdata.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++)
        {
            candidates.Add(Path.Combine(directory.FullName, "ocr", "tessdata"));
            directory = directory.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, $"{_options.Language}.traineddata")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Нет файла {_options.Language}.traineddata. Положите его в ocr/tessdata " +
            "или задайте Npa:Ocr:TessdataPath (скрипт scripts/fetch-tessdata.ps1).");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Временный файл удалит очистка рабочего каталога.
        }
    }
}
