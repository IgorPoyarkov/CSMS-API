namespace Csms.Application;

/// <summary>
/// Валидация ответа арбитра. Главные инварианты: только кандидаты из пула,
/// одна оценка на норму, match_degree ≠ условия наступления ответственности,
/// и всё спорное уходит на ручную проверку.
/// </summary>
public sealed class MappingValidator : IMappingValidator
{
    public ValidationOutcome Validate(
        LlmArbiterResponse response,
        IReadOnlyList<LiabilityCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(candidates);

        var violations = new List<string>();
        var hasHardViolations = false;
        var needsManualReviewFallback = false;
        var byId = candidates.ToDictionary(
            candidate => candidate.LiabilityNodeId,
            StringComparer.Ordinal);

        // Дубликаты и нормы вне пула разбираем до скоринга: они не должны
        // незаметно «поглотить» корректную оценку другой нормы.
        var unique = new Dictionary<string, LlmArbiterItem>(StringComparer.Ordinal);
        foreach (var item in response.Results)
        {
            if (string.IsNullOrWhiteSpace(item.LiabilityNodeId))
            {
                violations.Add("Арбитр вернул оценку без liability_node_id.");
                hasHardViolations = true;
                continue;
            }

            if (!byId.ContainsKey(item.LiabilityNodeId))
            {
                violations.Add($"Арбитр вернул норму вне пула кандидатов: {item.LiabilityNodeId}.");
                hasHardViolations = true;
                continue;
            }

            if (unique.TryGetValue(item.LiabilityNodeId, out var previous))
            {
                var conflict = previous.MatchDegree != item.MatchDegree ||
                               Math.Abs(previous.ProbabilityScore - item.ProbabilityScore) > 0.15;
                var winner = item.ProbabilityScore >= previous.ProbabilityScore ? item : previous;
                unique[item.LiabilityNodeId] = conflict ? WithReview(winner) : winner;
                violations.Add(
                    $"Дубликат оценки для {item.LiabilityNodeId}" +
                    (conflict ? ": оценки конфликтуют, нужна ручная проверка." : "."));
                needsManualReviewFallback = true;
                continue;
            }

            unique.Add(item.LiabilityNodeId, item);
        }

        var accepted = new List<MatchedLiability>();
        foreach (var (nodeId, item) in unique)
        {
            var candidate = byId[nodeId];
            if (item.MatchDegree == MatchDegree.Irrelevant)
            {
                // Irrelevant исключается из вывода, но остаётся в аудите.
                continue;
            }

            var review = item.RequiresHumanReview;
            var conditions = item.QualifyingConditions?
                                 .Where(text => !string.IsNullOrWhiteSpace(text))
                                 .Select(text => text.Trim())
                                 .Distinct(StringComparer.Ordinal)
                                 .ToArray()
                             ?? [];

            if (item.QualifyingConditions is null)
            {
                violations.Add($"У нормы {nodeId} отсутствует qualifying_conditions.");
                review = true;
            }

            var probability = item.ProbabilityScore;
            if (double.IsNaN(probability) || probability is < 0 or > 1)
            {
                violations.Add(
                    $"У нормы {nodeId} probability_score={item.ProbabilityScore} вне диапазона 0..1: значение ограничено.");
                probability = Math.Clamp(
                    double.IsNaN(probability) ? 0 : probability, 0, 1);
                review = true;
            }

            if (probability < LiabilityMappingRules.LowProbabilityThreshold)
            {
                review = true;
            }

            if (item.MatchDegree == MatchDegree.Blanket ||
                (item.MatchDegree == MatchDegree.Partial &&
                 probability < LiabilityMappingRules.PartialReviewThreshold))
            {
                review = true;
            }

            if (string.IsNullOrWhiteSpace(item.SanctionSummary) ||
                string.IsNullOrWhiteSpace(item.LegalReasoning))
            {
                violations.Add($"У нормы {nodeId} пустое sanction_summary или legal_reasoning.");
                review = true;
            }

            if (IsMetadataIncomplete(candidate))
            {
                violations.Add($"У нормы {nodeId} неполные метаданные: статус, дата редакции или реквизиты.");
                review = true;
            }

            accepted.Add(new MatchedLiability(
                nodeId,
                candidate.NormReference,
                item.MatchDegree,
                probability,
                conditions,
                string.IsNullOrWhiteSpace(item.SanctionSummary)
                    ? "Не указано арбитром; требуется ручная проверка."
                    : item.SanctionSummary.Trim(),
                string.IsNullOrWhiteSpace(item.LegalReasoning)
                    ? "Арбитр не привёл обоснование; требуется ручная проверка."
                    : item.LegalReasoning.Trim(),
                review));
        }

        // Пропущенный кандидат не должен исчезать: особенно это важно для
        // общих/бланкетных норм, которые модель охотнее всего игнорирует.
        foreach (var candidate in candidates)
        {
            if (unique.ContainsKey(candidate.LiabilityNodeId))
            {
                continue;
            }

            var degree = candidate.NormType is LiabilityNormType.General or LiabilityNormType.Blanket
                ? MatchDegree.Blanket
                : MatchDegree.Partial;
            accepted.Add(new MatchedLiability(
                candidate.LiabilityNodeId,
                candidate.NormReference,
                degree,
                0,
                [],
                "Требуется ручная проверка.",
                $"Арбитр не вернул оценку для кандидата {candidate.LiabilityNodeId}; " +
                "норма сохранена как предварительная.",
                true));
            violations.Add($"Арбитр не оценил кандидата {candidate.LiabilityNodeId}.");
            hasHardViolations = true;
            needsManualReviewFallback = true;
        }

        return new ValidationOutcome(
            accepted
                .OrderByDescending(match => match.ProbabilityScore)
                .ThenBy(match => match.LiabilityNodeId, StringComparer.Ordinal)
                .ToArray(),
            violations,
            needsManualReviewFallback,
            hasHardViolations);
    }

    private static bool IsMetadataIncomplete(LiabilityCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Status) ||
        candidate.EditionDate is null ||
        string.IsNullOrWhiteSpace(candidate.NormReference) ||
        string.IsNullOrWhiteSpace(candidate.Text);

    private static LlmArbiterItem WithReview(LlmArbiterItem item) =>
        item with { RequiresHumanReview = true };
}
