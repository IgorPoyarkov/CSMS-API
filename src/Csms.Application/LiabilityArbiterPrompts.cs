using System.Text.Encodings.Web;
using System.Text.Json;

namespace Csms.Application;

/// <summary>Промпты правового арбитра: только переданный пул, строгий JSON.</summary>
public static class LiabilityArbiterPrompts
{
    public const string SystemPrompt = """
        Ты — правовой арбитр по сопоставлению структурированной обязанности (Сущность А)
        и норм об ответственности (Сущность Б). Оцени ТОЛЬКО тот пул кандидатов,
        который передан в пользовательском сообщении.

        КАТЕГОРИЧЕСКИЕ ЗАПРЕТЫ:
        - не добавляй статьи, нормы, реквизиты, даты и санкции, которых нет во входных данных;
        - не используй внешние знания и не «вспоминай» кодекс или закон наизусть;
        - не возвращай нормы с liability_node_id вне переданного пула;
        - не выдумывай norm_reference и текст нормы;
        - не пиши ничего вне JSON.

        На входе: duty (обязанность), hypotheses (3–4 гипотезы нарушения),
        candidates (нормы-кандидаты с реквизитами и текстом).

        Для КАЖДОГО кандидата определи:
        1) match_degree — ТОЛЬКО факт совпадения состава нормы с гипотезой нарушения:
           - full — состав прямо и конкретно описывает неисполнение или ненадлежащее
             исполнение именно этой обязанности (совпадают действие, объект, субъект и срок);
           - partial — норма охватывает только один аспект обязанности либо требует
             дополнительных последствий, ущерба или иных условий;
           - blanket — специального состава нет, но обязанность подпадает под общую норму
             об ответственности за нарушение требований данного вида законодательства;
           - irrelevant — норма не относится к обязанности и исключается из итогового вывода.
        2) probability_score — число от 0.0 до 1.0: насколько применима норма.
        3) qualifying_conditions — массив строк: условия, при которых наступает
           ответственность (ущерб, вина, последствия, дополнительный факт, срок).
           ПУСТОЙ массив, если дополнительных условий нет. НЕ смешивай это с match_degree:
           match_degree — что совпало, qualifying_conditions — при чём наступает ответственность.
        4) sanction_summary — кратко, только из текста нормы.
        5) legal_reasoning — обоснование со ссылкой на текст кандидата и гипотезу.
        6) requires_human_review — true при низкой уверенности (ниже 0.65), правовой
           неоднозначности, конфликте норм, неполных метаданных или если ты не уверен(а).

        Верни ТОЛЬКО валидный JSON без markdown-обёрток и без пояснений:
        {"results":[{"liability_node_id":"...","match_degree":"full|partial|blanket|irrelevant",
        "probability_score":0.0,"qualifying_conditions":["..."],"sanction_summary":"...",
        "legal_reasoning":"...","requires_human_review":false}]}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        // Промпт читает и модель, и человек в аудите: кириллица должна оставаться
        // кириллицей, а не последовательностью \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сборка пользовательского сообщения для одного батча.</summary>
    public static string BuildUserPrompt(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        IReadOnlyList<LiabilityCandidate> candidates,
        string feedback = "")
    {
        var candidatePayload = candidates.Select(candidate => new
        {
            liability_node_id = candidate.LiabilityNodeId,
            norm_reference = candidate.NormReference,
            text = Truncate(candidate.Text, LiabilityBatching.MaxCandidateTextChars),
            act_code = candidate.ActCode,
            act_version = candidate.ActVersion,
            norm_type = candidate.NormType.ToString(),
            status = candidate.Status,
            edition_date = candidate.EditionDate?.ToString("yyyy-MM-dd"),
            metadata = candidate.Metadata,
        });

        var payload = new
        {
            duty = new
            {
                duty_id = duty.Identifiers.DutyId,
                statement = duty.Statement,
                @object = duty.Object,
                subject = duty.Subject,
                deadline = duty.Deadline,
                condition = duty.Condition,
                source_document_id = duty.Identifiers.SourceDocumentId,
                article = duty.Identifiers.Article,
                clause = duty.Identifiers.Clause,
                item = duty.Identifiers.Item,
                external_id = duty.Identifiers.ExternalId,
            },
            hypotheses = hypotheses.Select(hypothesis => new
            {
                id = hypothesis.Id,
                kind = hypothesis.Kind.ToString(),
                text = hypothesis.Text,
            }),
            candidate_count = candidates.Count,
            candidates = candidatePayload,
            instruction = "Верни оценку для каждого liability_node_id из этого батча, " +
                          "без пояснений и без выдуманных норм.",
        };

        var prompt = JsonSerializer.Serialize(payload, JsonOptions);
        return string.IsNullOrWhiteSpace(feedback)
            ? prompt
            : $"ПРЕДЫДУЩАЯ ПОПЫТКА НЕ ПРОЙДЕНА: {feedback}\nВерни тот же JSON строго по схеме.\n{prompt}";
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + " … [текст усечён для контекста]";
    }
}
