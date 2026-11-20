using System.Text.Json;

namespace Csms.Application;

/// <summary>
/// Строгий разбор ответа арбитра. Любое неизвестное поле, комментарий,
/// строковый enum вне справочника или неверный тип = ошибка валидации.
/// Никаких «вырезаем JSON между скобками» — это маскирует галлюцинации.
/// </summary>
public static class LlmArbiterResponseParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private static readonly HashSet<string> ItemFields =
    [
        "liability_node_id", "match_degree", "probability_score",
        "qualifying_conditions", "sanction_summary", "legal_reasoning",
        "requires_human_review"
    ];

    public static bool TryParse(
        string content,
        out LlmArbiterResponse response,
        out string error)
    {
        response = new LlmArbiterResponse([]);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "LLM вернула пустой ответ.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Корень ответа должен быть объектом.";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "results")
                {
                    error = $"Неизвестное поле в корне ответа: {property.Name}.";
                    return false;
                }
            }

            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                error = "Ожидался массив results.";
                return false;
            }

            var items = new List<LlmArbiterItem>();
            foreach (var element in results.EnumerateArray())
            {
                if (!TryReadItem(element, out var item, out error))
                {
                    return false;
                }

                items.Add(item);
            }

            response = new LlmArbiterResponse(items);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Ответ не является корректным JSON: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadItem(
        JsonElement element,
        out LlmArbiterItem item,
        out string error)
    {
        item = new LlmArbiterItem(
            string.Empty, MatchDegree.Irrelevant, 0, [], string.Empty, string.Empty, true);
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Каждый элемент results должен быть объектом.";
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!ItemFields.Contains(property.Name))
            {
                error = $"Неизвестное поле в оценке нормы: {property.Name}.";
                return false;
            }
        }

        if (!TryString(element, "liability_node_id", out var nodeId, out error) ||
            !TryEnum(element, "match_degree", out var degree, out error) ||
            !TryNumber(element, "probability_score", out var probability, out error) ||
            !TryString(element, "sanction_summary", out var sanction, out error) ||
            !TryString(element, "legal_reasoning", out var reasoning, out error))
        {
            return false;
        }

        if (!element.TryGetProperty("qualifying_conditions", out var conditions) ||
            conditions.ValueKind == JsonValueKind.Null)
        {
            error = "qualifying_conditions обязателен и должен быть массивом (можно пустым).";
            return false;
        }

        if (conditions.ValueKind != JsonValueKind.Array)
        {
            error = "qualifying_conditions должен быть массивом строк.";
            return false;
        }

        var conditionList = new List<string>();
        foreach (var condition in conditions.EnumerateArray())
        {
            if (condition.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(condition.GetString()))
            {
                error = "qualifying_conditions содержит пустую или нестроковую запись.";
                return false;
            }

            conditionList.Add(condition.GetString()!.Trim());
        }

        if (!element.TryGetProperty("requires_human_review", out var review) ||
            review.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "requires_human_review должен быть boolean.";
            return false;
        }

        if (probability is < 0 or > 1 || double.IsNaN(probability))
        {
            error = $"probability_score {probability} вне диапазона 0..1.";
            return false;
        }

        item = new LlmArbiterItem(
            nodeId, degree, probability, conditionList, sanction, reasoning, review.GetBoolean());
        return true;
    }

    private static bool TryString(
        JsonElement element, string name, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"Поле {name} обязательно и должно быть непустой строкой.";
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private static bool TryNumber(
        JsonElement element, string name, out double value, out string error)
    {
        value = 0;
        error = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out value))
        {
            error = $"Поле {name} должно быть числом.";
            return false;
        }

        return true;
    }

    private static bool TryEnum(
        JsonElement element, string name, out MatchDegree value, out string error)
    {
        value = MatchDegree.Irrelevant;
        error = string.Empty;
        if (!TryString(element, name, out var text, out error))
        {
            return false;
        }

        if (!Enum.TryParse(text, ignoreCase: true, out value) ||
            !Enum.IsDefined(value))
        {
            error = $"Поле {name} содержит неизвестное значение «{text}».";
            return false;
        }

        return true;
    }
}
