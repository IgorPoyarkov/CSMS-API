using System.Globalization;

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
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Размер контекста Ollama: промпт с 4 гипотезами и 8 нормами вместе с
    /// требованием цитат легко выходит за стандартные 4096 токенов.
    /// </summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>Дополнительные попытки после первой (итого 1 + MaxRetries).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Базовая задержка экспоненциального retry, миллисекунды.</summary>
    public int RetryBaseDelayMs { get; set; } = 2000;

    /// <summary>
    /// Температура арбитра. По умолчанию 0 — иначе один и тот же пул даёт
    /// воспроизводимую оценку; ненулевое значение допустимо для экспериментов
    /// и обязательно попадает в ключ запуска.
    /// </summary>
    public double Temperature { get; set; }

    /// <summary>
    /// Версия конфигурации конвейера. Увеличивать при ручном изменении
    /// настроек, которые не отражены в остальных полях отпечатка.
    /// </summary>
    public string ConfigVersion { get; set; } = "liability-config-v3";

    /// <summary>Стабильный отпечаток настроек модели для ключа запуска.</summary>
    public string BuildConfigFingerprint() =>
        string.Join('|',
            ConfigVersion,
            Model,
            EmbeddingModel,
            Temperature.ToString("0.00", CultureInfo.InvariantCulture),
            MaxOutputTokens,
            ContextTokens,
            MaxRetries,
            RetryBaseDelayMs);

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

        if (ContextTokens is < 2048 or > 32_768)
        {
            throw new InvalidOperationException("LiabilityMapping:ContextTokens должен быть 2048..32768.");
        }

        if (MaxRetries is < 0 or > 4)
        {
            throw new InvalidOperationException("LiabilityMapping:MaxRetries должен быть 0..4.");
        }

        if (Temperature is < 0 or > 1)
        {
            throw new InvalidOperationException("LiabilityMapping:Temperature должен быть 0..1.");
        }

        if (string.IsNullOrWhiteSpace(ConfigVersion))
        {
            throw new InvalidOperationException("LiabilityMapping:ConfigVersion не задана.");
        }
    }
}
