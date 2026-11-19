using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Рендер страниц PDF в BMP для распознавания. PDF с портала — это сканы, у
/// которых MediaBox задан в «пикселях при 100 dpi»; поэтому масштаб считается
/// от 72 dpi, иначе pdfium пытается выделить растр 60 000 × 84 000 пикселей
/// и падает с «failed to create a bitmap object».
/// </summary>
public sealed class PdfDocumentRenderer : IDisposable
{
    private readonly IDocReader _reader;

    private PdfDocumentRenderer(IDocReader reader, int dpi)
    {
        _reader = reader;
        Dpi = dpi;
        PageCount = reader.GetPageCount();
    }

    public int PageCount { get; }

    public int Dpi { get; }

    public static PdfDocumentRenderer Open(byte[] pdf, int dpi)
    {
        var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(dpi / 72.0));
        return new PdfDocumentRenderer(reader, dpi);
    }

    /// <summary>Рендерит страницу (0-based) в BMP 24 бита без сжатия.</summary>
    public RenderedPage RenderPage(int index, string targetPath)
    {
        using var page = _reader.GetPageReader(index);
        var pixels = page.GetImage();
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();

        WriteBmp(targetPath, pixels, width, height);
        return new RenderedPage(index + 1, targetPath, width, height);
    }

    /// <summary>BMP снизу вверх, строки выровнены по 4 байта. Без сторонних библиотек.</summary>
    internal static void WriteBmp(string path, byte[] bgra, int width, int height)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowSize * height;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(14 + 40 + pixelBytes);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var pixel = source + (x * 4);
                row[x * 3] = bgra[pixel];
                row[(x * 3) + 1] = bgra[pixel + 1];
                row[(x * 3) + 2] = bgra[pixel + 2];
            }

            writer.Write(row);
        }
    }

    public void Dispose() => _reader.Dispose();
}
