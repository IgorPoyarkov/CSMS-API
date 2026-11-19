using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Каталог схем проверяем на небольшой синтетической XSD: это не схема ЕИС,
/// а проверка того, что загрузка, кеш и валидация работают до появления
/// настоящего Альбома форматов.
/// </summary>
public sealed class EisSchemaCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csms-schema-tests-" + Guid.NewGuid().ToString("N"));

    private const string Schema = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="doc">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="number" type="xs:string"/>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    [Fact]
    public void Без_схем_каталог_честно_сообщает_что_проверять_нечем()
    {
        var catalog = new EisSchemaCatalog(_root);

        Assert.False(catalog.HasSchema("14.2"));
        Assert.Empty(catalog.Validate("<doc><number>1</number></doc>", "14.2"));
    }

    [Fact]
    public void Схема_версии_загружается_и_проверяет_XML()
    {
        var versionDir = Path.Combine(_root, "14.2");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "doc.xsd"), Schema);
        var catalog = new EisSchemaCatalog(_root);

        Assert.True(catalog.HasSchema("14.2"));
        Assert.Empty(catalog.Validate("<doc><number>1</number></doc>", "14.2"));

        var errors = catalog.Validate("<doc><other>1</other></doc>", "14.2");
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains("XSD", StringComparison.Ordinal));
    }

    [Fact]
    public void Версия_не_может_выйти_за_пределы_каталога_схем()
    {
        var catalog = new EisSchemaCatalog(_root);

        Assert.False(catalog.HasSchema("../evil"));
        Assert.Empty(catalog.Validate("<doc/>", "../evil"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
