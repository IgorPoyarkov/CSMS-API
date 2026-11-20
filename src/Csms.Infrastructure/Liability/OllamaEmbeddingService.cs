using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Эмбеддинги той же локальной Ollama. Вектор нужен только для поиска:
/// в базу пишется готовый вектор, текст остаётся в liability_norms.
/// </summary>
public sealed class OllamaEmbeddingService(
    LiabilityMappingOptions options,
    ILogger<OllamaEmbeddingService> logger) : ILiabilityEmbeddingService
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(options.Endpoint),
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
    };

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var payload = new { model = options.EmbeddingModel, input = text };
        using var reply = await _client.PostAsJsonAsync("api/embed", payload, cancellationToken);
        if (!reply.IsSuccessStatusCode)
        {
            var body = await reply.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama embeddings вернул {(int)reply.StatusCode}: {Truncate(body, 400)}");
        }

        using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("embeddings", out var embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array ||
            embeddings.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Ollama embeddings вернула пустой embeddings[].");
        }

        var vector = embeddings[0]
            .EnumerateArray()
            .Select(value => value.GetSingle())
            .ToArray();
        if (vector.Length == 0 || vector.Any(float.IsNaN))
        {
            throw new InvalidOperationException("Ollama embeddings вернула некорректный вектор.");
        }

        logger.LogDebug("Получен эмбеддинг размерности {Dimensions}", vector.Length);
        return vector;
    }

    /// <summary>Классический pgvector-литерал: [0.1,0.2,...].</summary>
    public static string ToPgVector(float[] vector) =>
        "[" + string.Join(",", vector.Select(value =>
            value.ToString("0.########", CultureInfo.InvariantCulture))) + "]";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
