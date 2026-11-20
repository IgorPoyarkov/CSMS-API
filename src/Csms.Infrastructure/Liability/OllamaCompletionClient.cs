using System.Net.Http.Json;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Вызов локального арбитра через OpenAI-совместимый эндпоинт Ollama.
/// Температура по умолчанию равна нулю: один и тот же пул должен давать
/// воспроизводимую оценку, иначе идемпотентность run_id теряет смысл.
/// Любая смена температуры попадает в отпечаток конфигурации и в аудит.
/// </summary>
public sealed class OllamaCompletionClient(
    LiabilityMappingOptions options,
    ILogger<OllamaCompletionClient> logger) : ILlmCompletionClient
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(options.Endpoint),
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
    };

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var contextTokens = options.ContextTokens;
        var payload = new
        {
            model = options.Model,
            temperature = options.Temperature,
            stream = false,
            max_tokens = options.MaxOutputTokens,
            options = new { num_ctx = contextTokens },
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var reply = await _client.PostAsJsonAsync(
            "v1/chat/completions", payload, cancellationToken);
        if (!reply.IsSuccessStatusCode)
        {
            var body = await reply.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama вернул {(int)reply.StatusCode}: {Truncate(body, 400)}");
        }

        var content = await reply.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(content);
        var message = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        if (!message.TryGetProperty("content", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Ollama вернула ответ без текстового content.");
        }

        var answer = text.GetString() ?? string.Empty;
        logger.LogDebug("Арбитр ответил, длина={Length}, превью={Preview}", answer.Length, Truncate(answer, 500));
        return answer;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
