namespace Csms.Infrastructure.Liability;

/// <summary>
/// Настройки локального контура сопоставления обязанностей и норм ответственности.
/// Внешние ключи и сеть не нужны: и эмбеддинги, и арбитр крутятся на Ollama.
/// </summary>
public sealed class LiabilityMappingOptions
{
    public const string SectionName = "LiabilityMapping";

    /// <summary>OpenAI-совместимый эндпоинт Ollama.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Модель-арбитр с strictly JSON режимом.</summary>
    public string Model { get; set; } = "qwen2.5:14b-instruct-q4_K_M";

    /// <summary>Локальная модель эмбеддингов (размерность 768).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Таймаут одного вызова модели.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Максимум выходных токенов арбитра.</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>Дополнительные попытки после первой (итого 1 + MaxRetries).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Базовая задержка экспоненциального retry, миллисекунды.</summary>
    public int RetryBaseDelayMs { get; set; } = 2000;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("LiabilityMapping:Endpoint должен быть абсолютным URL.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("LiabilityMapping:Model не задана.");
        }

        if (string.IsNullOrWhiteSpace(EmbeddingModel))
        {
            throw new InvalidOperationException("LiabilityMapping:EmbeddingModel не задана.");
        }

        if (TimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException("LiabilityMapping:TimeoutSeconds должен быть 1..3600.");
        }

        if (MaxOutputTokens is < 256 or > 16_384)
        {
            throw new InvalidOperationException("LiabilityMapping:MaxOutputTokens должен быть 256..16384.");
        }

        if (MaxRetries is < 0 or > 4)
        {
            throw new InvalidOperationException("LiabilityMapping:MaxRetries должен быть 0..4.");
        }
    }
}
