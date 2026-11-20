namespace Csms.Infrastructure.Documents;

/// <summary>
/// Настройки агента извлечения функций из документов. Работает по запросу,
/// без фонового воркера: тяжёлую модель не держим загруженной просто так.
/// </summary>
public sealed class DocumentFunctionsOptions
{
    public const string SectionName = "DocumentFunctions";

    /// <summary>OpenAI-совместимый эндпоинт локальной модели.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen2.5:14b-instruct-q4_K_M";

    /// <summary>
    /// Таймаут одного запроса к модели. 14B на одном сегменте может думать
    /// дольше пяти минут, поэтому запас — 15 минут.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Жёсткий лимит генерации. Без него модель иногда уходит в длинный
    /// поток токенов и запрос упирается в HttpClient.Timeout.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Сегментов в одном запросе. Модель запущена с num_ctx=8192: даже 4
    /// сегмента с цепочкой предков дают 10–13 тыс. токенов и получают 400.
    /// Поэтому по умолчанию один сегмент на запрос — дороже по времени,
    /// но покрытие документа остаётся полным.
    /// </summary>
    public int BatchSize { get; set; } = 1;

    /// <summary>Сколько раз повторять запрос при невалидном JSON.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Разрешение рендера PDF для OCR.</summary>
    public int Dpi { get; set; } = 300;

    /// <summary>Защита от случайной многостраничной загрузки.</summary>
    public int MaxPages { get; set; } = 400;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "DocumentFunctions:Endpoint должен быть абсолютным адресом.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("DocumentFunctions:Model не задан.");
        }

        if (TimeoutSeconds is < 30 or > 3600)
        {
            throw new InvalidOperationException(
                "DocumentFunctions:TimeoutSeconds должен быть в диапазоне 30..3600.");
        }

        if (MaxOutputTokens is < 256 or > 8192)
        {
            throw new InvalidOperationException(
                "DocumentFunctions:MaxOutputTokens должен быть в диапазоне 256..8192.");
        }

        if (BatchSize is < 1 or > 16)
        {
            throw new InvalidOperationException(
                "DocumentFunctions:BatchSize должен быть в диапазоне 1..16.");
        }

        if (MaxRetries is < 0 or > 5)
        {
            throw new InvalidOperationException(
                "DocumentFunctions:MaxRetries должен быть в диапазоне 0..5.");
        }
    }
}
