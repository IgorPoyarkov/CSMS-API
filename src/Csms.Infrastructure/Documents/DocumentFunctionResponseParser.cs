using System.Text.Json;
using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Разбор ответа модели. Модель может обернуть JSON в markdown или добавить
/// пояснение — всё это отрезается до JsonDocument, а недостающие поля
/// получают безопасные значения по умолчанию.
/// </summary>
public static class DocumentFunctionResponseParser
{
    public static IReadOnlyList<DocumentSegmentExtraction> Parse(
        string content,
        IReadOnlyList<DocumentSegment> batch)
    {
        var payload = ExtractJson(content);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Ответ модели без массива results.");
        }

        var byId = batch.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var parsed = new List<DocumentSegmentExtraction>();

        foreach (var item in results.EnumerateArray())
        {
            var segmentId = Text(item, "segment_id");
            if (string.IsNullOrWhiteSpace(segmentId) || !byId.TryGetValue(segmentId, out var segment))
                continue; // модели нельзя добавлять сегменты, которых не было на входе

            var functions = new List<ExtractedDocumentFunction>();
            if (item.TryGetProperty("extracted_functions", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var function in array.EnumerateArray())
                {
                    functions.Add(new ExtractedDocumentFunction(
                        Text(function, "function_id") ?? "F1",
                        Text(function, "parent_function_title") ?? "Функция",
                        Text(function, "category") ?? DocumentFunctionTaxonomy.Other,
                        Number(function, "category_confidence"),
                        Text(function, "suggested_category"),
                        Text(function, "actor_role"),
                        Text(function, "obligation") ?? DocumentFunctionObligations.Mandatory,
                        Bool(function, "is_decomposed"),
                        ParseSubtasks(function)));
                }
            }

            var status = Text(item, "status") ?? "extracted";
            if (functions.Count == 0)
                status = "informational";

            parsed.Add(new DocumentSegmentExtraction(
                segment.SegmentId,
                segment.SourcePath,
                status,
                functions,
                new DocumentSegmentReport(1, functions.Count == 0 ? 0 : 1,
                    functions.Count == 0, CountCandidates(functions), 0,
                    CountNeedsReview(functions))));
        }

        // Невернувшиеся сегменты не пропадают: они попадают в отчёт как
        // informational, и coverage ledger остаётся честным.
        foreach (var segment in batch.Where(s => parsed.All(p => p.SegmentId != s.SegmentId)))
        {
            parsed.Add(new DocumentSegmentExtraction(
                segment.SegmentId,
                segment.SourcePath,
                "informational",
                [],
                new DocumentSegmentReport(1, 0, true, 0, 0, 0)));
        }

        return parsed;
    }

    private static IReadOnlyList<ExtractedDocumentSubtask> ParseSubtasks(JsonElement function)
    {
        var result = new List<ExtractedDocumentSubtask>();
        if (!function.TryGetProperty("subtasks", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            result.Add(new ExtractedDocumentSubtask(
                Text(item, "subtask_id") ?? "F1.1",
                Text(item, "action_verb") ?? string.Empty,
                Text(item, "target_object") ?? string.Empty,
                Text(item, "description") ?? string.Empty,
                Text(item, "trigger_condition"),
                Text(item, "deadline_rule"),
                Text(item, "source_quote") ?? string.Empty,
                Bool(item, "needs_review"),
                Number(item, "confidence")));
        }

        return result;
    }

    private static int CountCandidates(IEnumerable<ExtractedDocumentFunction> functions) =>
        functions.Sum(f => f.Subtasks.Count);

    private static int CountNeedsReview(IEnumerable<ExtractedDocumentFunction> functions) =>
        functions.Sum(f => f.Subtasks.Count(s => s.NeedsReview));

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        var open = trimmed.IndexOf('{');
        var close = trimmed.LastIndexOf('}');
        if (open >= 0 && close > open)
            return trimmed[open..(close + 1)];
        return trimmed;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
        value.GetBoolean();

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : 0;
}
