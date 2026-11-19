using System.IO.Compression;
using System.Text;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Упаковка подписанного пакета в архив ЕИС: XML + отсоединённая подпись.
///
/// Архив собирается в памяти — временные файлы с подписью и данными заказчика
/// не должны оседать на диске. Порядок и имена файлов фиксированы, чтобы
/// настоящий транспорт и тесты видели один и тот же состав пакета.
/// </summary>
public static class EisPackageArchiver
{
    public static byte[] BuildZip(EisPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(package.SignatureBase64))
        {
            throw new DomainException("Нельзя упаковать неподписанный пакет.");
        }

        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(package.SignatureBase64.Trim());
        }
        catch (FormatException)
        {
            throw new DomainException("Подпись пакета не является корректным base64: проверьте ответ плагина ЭЦП.");
        }

        using var stream = new MemoryStream();
        var fileName = $"{package.ContractEisNumber}_{package.Id:N}";

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, fileName + ".xml", Encoding.UTF8.GetBytes(package.PayloadXml));
            Write(archive, fileName + ".sig", signature);
        }

        return stream.ToArray();
    }

    public static string BuildBase64Zip(EisPackage package) => Convert.ToBase64String(BuildZip(package));

    private static void Write(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
