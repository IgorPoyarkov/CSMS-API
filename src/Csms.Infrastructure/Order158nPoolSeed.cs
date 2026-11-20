using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csms.Application;

namespace Csms.Infrastructure;

/// <summary>Содержимое эталонного файла пула 158н.</summary>
public sealed record Order158nSeedFile(
    string Version,
    string SourceDocument,
    string? SourceUrl,
    DateTimeOffset PublishedAt,
    IReadOnlyList<Order158nPoolItemInfo> Items);

/// <summary>
/// Первичное заполнение справочника <c>order158nPool</c> эталоном из официального
/// приказа. Читается один раз при отсутствии активной версии в БД; дальше
/// рабочим источником остаётся хранилище справочников.
/// </summary>
public static class Order158nPoolSeed
{
    public const string ResourceName = "Csms.Infrastructure.Seeds.order158n-pool.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static (Order158nSeedFile File, string Sha256) Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"В сборке нет ресурса {ResourceName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var file = JsonSerializer.Deserialize<Order158nSeedFile>(json, Options)
            ?? throw new InvalidOperationException("Эталон пула 158н пуст или повреждён.");

        return (file, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
}
