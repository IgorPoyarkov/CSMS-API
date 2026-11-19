using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EisDocGenerator;

/// <summary>
/// Генератор документации по XSD Альбома ЕИС.
///
/// Выводит два файла:
///   1. JSON-каталог для обозревателя («Swagger для схем ЕИС»);
///   2. TypeScript-объявления со всеми типами и корневыми элементами.
///
/// Ничего не скачивает: вход — уже установленная папка schemas/&lt;версия&gt;.
/// </summary>
internal static class Program
{
    // Без BOM: JSON из public/ читается браузером напрямую, BOM мешает JSON.parse.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static int Main(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine(
                "Использование: EisDocGenerator <папка_XSD> <catalog.json> <types.d.ts> " +
                "<версия> <id_документа> <sha256>");
            return 2;
        }

        var xsdDirectory = args[0];
        var catalogPath = args[1];
        var typesPath = args[2];
        var version = args[3];
        var sourceId = args[4];
        var sha256 = args[5];

        if (!Directory.Exists(xsdDirectory))
        {
            Console.Error.WriteLine($"Папка XSD не найдена: {xsdDirectory}");
            return 2;
        }

        var catalog = new SchemaReader().Read(xsdDirectory, version, sourceId, sha256);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(typesPath))!);

        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, JsonOptions), Utf8);
        File.WriteAllText(typesPath, BuildTypes(catalog), Utf8);

        Console.WriteLine(
            $"Готово: {catalog.XsdFileCount} XSD, {catalog.Types.Count} определений, " +
            $"{catalog.Namespaces.Count} пространств имён.");
        Console.WriteLine($"  {catalogPath}");
        Console.WriteLine($"  {typesPath}");

        return 0;
    }

    private static string BuildTypes(SchemaCatalog catalog)
    {
        var result = new StringBuilder();
        result.AppendLine("// АВТОСГЕНЕРИРОВАНО генератором csms-api/tools/EisDocGenerator.");
        result.AppendLine("// Не редактируйте вручную: правки будут потеряны при обновлении XSD.");
        result.AppendLine($"// Альбом ЕИС {catalog.Version}, документ {catalog.SourceId}, " +
                          $"SHA-256 {catalog.Sha256[..Math.Min(12, catalog.Sha256.Length)]}…");
        result.AppendLine($"// Создано {catalog.GeneratedAt:u}, файлов XSD: {catalog.XsdFileCount}.");
        result.AppendLine();

        foreach (var definition in catalog.Types)
        {
            AppendDocumentation(result, definition.Documentation);

            switch (definition.Kind)
            {
                case "complex":
                {
                    var baseName = definition.Base;
                    var canExtend = !string.IsNullOrEmpty(baseName) &&
                                    baseName != definition.Name &&
                                    Regex.IsMatch(baseName, "^[A-Za-z_$][A-Za-z0-9_$]*$") &&
                                    baseName is not ("string" or "number" or "boolean" or "unknown" or "any");

                    if (canExtend)
                    {
                        result.AppendLine($"export interface {definition.Name} extends {baseName} {{");
                    }
                    else
                    {
                        result.AppendLine($"export interface {definition.Name} {{");
                    }

                    foreach (var field in definition.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
                    {
                        AppendDocumentation(result, field.Documentation, "  ");
                        var type = field.Array ? $"Array<{field.Type}>" : field.Type;
                        result.AppendLine($"  {JsonSerializer.Serialize(field.Name, JsonOptions)}{field.OptionalText()}: {type};");
                    }

                    result.AppendLine("}");
                    break;
                }

                case "enum" when definition.EnumValues.Count > 0:
                {
                    var values = definition.EnumValues
                        .Select(value => JsonSerializer.Serialize(value, JsonOptions));
                    result.AppendLine($"export type {definition.Name} = {string.Join(" | ", values)};");
                    break;
                }

                case "list":
                {
                    var item = string.IsNullOrEmpty(definition.Base) ? "unknown" : definition.Base;
                    result.AppendLine($"export type {definition.Name} = Array<{item}>;");
                    break;
                }

                default:
                {
                    var baseType = string.IsNullOrEmpty(definition.Base) || definition.Base == definition.Name
                        ? "unknown"
                        : definition.Base;
                    result.AppendLine($"export type {definition.Name} = {baseType};");
                    break;
                }
            }

            result.AppendLine();
        }

        return result.ToString();
    }

    private static void AppendDocumentation(StringBuilder result, string? text, string indent = "")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var safe = text.Replace("*/", "*\\/").Replace('\r', ' ').Replace('\n', ' ');
        result.AppendLine($"{indent}/** {safe} */");
    }

    private static string OptionalText(this SchemaField field) => field.Optional ? "?" : string.Empty;
}
