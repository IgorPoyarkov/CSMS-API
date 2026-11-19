using System.IO.Compression;
using System.Text;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>Проверка упаковки подписанного документа в архив ЕИС.</summary>
public sealed class EisPackageArchiverTests
{
    [Fact]
    public void Архив_содержит_XML_и_отсоединённую_подпись()
    {
        var package = SignedPackage(Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-signature")));

        var archiveBytes = EisPackageArchiver.BuildZip(package);

        using var stream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray();
        var prefix = $"{package.ContractEisNumber}_{package.Id:N}";

        Assert.Equal(new[] { prefix + ".sig", prefix + ".xml" }, names);

        using var xml = archive.GetEntry(prefix + ".xml")!.Open();
        using var reader = new StreamReader(xml);
        Assert.Equal(package.PayloadXml, reader.ReadToEnd());

        using var sig = archive.GetEntry(prefix + ".sig")!.Open();
        using var sigReader = new StreamReader(sig);
        Assert.Equal("fake-signature", sigReader.ReadToEnd());
    }

    [Fact]
    public void Неподписанный_пакет_не_упаковывается()
    {
        var package = EisPackage.Create(
            EisDocumentType.SmpAttraction,
            "0373100000123000001",
            "2612345678901234567890",
            "<doc/>",
            "placeholder-v0",
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => EisPackageArchiver.BuildZip(package));
    }

    private static EisPackage SignedPackage(string signature)
    {
        var package = EisPackage.Create(
            EisDocumentType.SmpAttraction,
            "0373100000123000001",
            "2612345678901234567890",
            "<doc><number>1</number></doc>",
            "placeholder-v0",
            DateTimeOffset.UtcNow);

        package.BeginSigning(DateTimeOffset.UtcNow);
        package.AttachSignature(signature, "thumbprint", package.PayloadSha256, DateTimeOffset.UtcNow);

        return package;
    }
}
