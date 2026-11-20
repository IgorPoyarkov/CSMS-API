using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Npa;

/// <summary>
/// Второй агент: LLM-разбор спорных строк НПА. Работает с локальной моделью
/// через OpenAI-совместимый эндпоинт Ollama, без ключей и внешней сети.
///
/// Главное отличие от первого агента — он не публикует правила молча. Код
/// принимается только если он был передан в списке кандидатов из справочника,
/// а цитата дословно есть в тексте акта. Любое несовпадение отбрасывается.
/// </summary>
public sealed class NpaLlmAnalyzer(
    NpaOptions options,
    ILogger<NpaLlmAnalyzer> logger)
{
    // Анализатор — синглтон, поэтому один HttpClient живёт всё время работы:
    // локальный Ollama не требует ключей, пул соединений здесь уместен.
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(options.Llm.Endpoint),
        Timeout = TimeSpan.FromSeconds(options.Llm.TimeoutSeconds),
    };

    private const string SystemPrompt = """
        Ты — эксперт-аналитик по государственным и корпоративным закупкам РФ
        (44-ФЗ, 223-ФЗ). Разбери спорные строки нормативного акта и определи,
        какие правила применяются к кодам ОКПД2.

        Правила:
        1. Наследование сверху вниз: код родительской группировки (2, 4 или 6
           знаков) действует на все дочерние коды до 9-го знака, если нет прямых
           исключений. Для 9-значного кода проверяй всех родителей. Разворачивай
           диапазоны вида 26.20.1 — 26.20.4.
        2. Сначала исключения: «кроме», «за исключением», «не распространяется»
           дают excluded. Примечания, звёздочки и сноски под таблицей имеют такую
           же силу, как основной текст.
        3. explicit — цифровой код присутствует в тексте; implicit — кода нет, но
           наименование или назначение совпало с описанием в акте. Никогда не
           выдумывай коды: бери только из предложенного для строки списка
           кандидатов. Для implicit обязательно ставь requires_human_review=true.
        4. Фиксируй условия: пороги НМЦК, способ определения поставщика, уровень
           бюджета и тип заказчика, специальные требования, рамочный характер
           акта — каждое условие отдельной строкой в conditions.

        Ответ — ТОЛЬКО валидный JSON без обёрток ```json и без пояснений:
        {"act_info":{"requisites":"вид, дата, номер и название акта","is_framework_act":false},"results":[{"okpd2_code":"код","item_name":"наименование","match_type":"explicit|implicit|excluded|not_found","flag_active":true,"has_conditions":false,"conditions":[],"source_quote":"точная цитата из строки акта","requires_human_review":false}]}
        Если строке не соответствует ни один кандидат, верни match_type="not_found"
        и okpd2_code=null. Для каждой спорной строки верни не больше одного результата.
        """;

    public async Task<NpaLlmAnalysisResult> AnalyzeAsync(
        NpaLlmAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.Llm.Enabled)
        {
            throw new InvalidOperationException(
                "LLM-аналитик выключен (Npa:Llm:Enabled=false).");
        }

        var suggestions = new List<NpaLlmSuggestion>();
        var rejected = 0;
        var errors = new List<string>();

        foreach (var batch in request.Rows.Chunk(options.Llm.BatchSize))
        {
            try
            {
                var content = await AskAsync(request, batch, cancellationToken);
                if (content is null)
                {
                    errors.Add("Модель вернула пустой ответ.");
                    continue;
                }

                var response = NpaLlmResponseParser.Parse(content);
                if (response?.Results is null)
                {
                    // Фрагмент ответа помогает понять, где маленькая модель
                    // нарушила формат, не сохраняя в лог весь текст акта.
                    logger.LogWarning(
                        "Акт {Code}: ответ модели без поля results: {Preview}",
                        request.Code,
                        Truncate(content, 400));
                    errors.Add("Модель вернула JSON без поля results.");
                    continue;
                }

                var (accepted, dropped) = NpaLlmResponseParser.Validate(response, batch);
                suggestions.AddRange(accepted);
                rejected += dropped;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Ошибка модели не должна ронять разбор акта: детерминированные
                // правила остаются прежними, а сбой виден в результате.
                logger.LogWarning(exception, "LLM-аналит акта {Code}: запрос к модели не удался", request.Code);
                errors.Add(exception.Message);
            }
        }

        return new NpaLlmAnalysisResult(
            request.Code,
            options.Llm.Model,
            request.Rows.Count,
            suggestions.Count,
            rejected,
            suggestions,
            errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private async Task<string?> AskAsync(
        NpaLlmAnalysisRequest request,
        NpaLlmInputRow[] batch,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = options.Llm.Model,
            temperature = 0,
            stream = false,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserPrompt(request, batch) },
            },
        };

        using var reply = await _client.PostAsJsonAsync(
            "v1/chat/completions", payload, cancellationToken);
        if (!reply.IsSuccessStatusCode)
        {
            var body = await reply.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama вернул {(int)reply.StatusCode}: {Truncate(body, 300)}");
        }

        using var document = JsonDocument.Parse(
            await reply.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }

    /// <summary>
    /// В user-сообщении передаём только спорные строки и существующие коды:
    /// модель не должна изобретать ОКПД2, для неявного совпадения есть из чего выбрать.
    /// </summary>
    private static string BuildUserPrompt(NpaLlmAnalysisRequest request, NpaLlmInputRow[] batch)
    {
        var prompt = new StringBuilder();
        prompt.Append("Акт: ").Append(request.Title);
        if (!string.IsNullOrWhiteSpace(request.Number))
        {
            prompt.Append(" № ").Append(request.Number);
        }

        prompt.AppendLine().AppendLine("Спорные строки акта:");
        for (var index = 0; index < batch.Length; index++)
        {
            var row = batch[index];
            prompt.Append(index + 1).Append(')');
            if (row.Page is not null)
            {
                prompt.Append(" стр. ").Append(row.Page);
            }

            if (row.RowIndex is not null)
            {
                prompt.Append(", строка ").Append(row.RowIndex);
            }

            prompt.Append(": «").Append(row.Text).AppendLine("»");
            prompt.Append("   Кандидаты ОКПД2: ");
            prompt.AppendLine(string.Join(
                "; ",
                row.Candidates.Select(candidate => $"{candidate.Code} — {candidate.Name}")));
        }

        return prompt.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
