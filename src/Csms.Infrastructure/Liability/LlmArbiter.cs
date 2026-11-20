using System.Diagnostics;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Этап 3: правовой арбитр. Каждый батч получает один и тот же системный
/// промпт, температуру 0 и строгую проверку JSON. Все попытки пишутся в аудит;
/// после исчерпания повторов норма не теряется, а помечается ручной проверкой.
/// </summary>
public sealed class LlmArbiter(
    ILlmCompletionClient completionClient,
    ILiabilityMappingStore store,
    LiabilityMappingOptions options,
    ILogger<LlmArbiter> logger) : ILlmArbiter
{
    public async Task<LlmArbiterResponse> ArbitrateAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        IReadOnlyList<LiabilityCandidate> candidates,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duty);
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(candidates);

        var results = new List<LlmArbiterItem>();
        var batches = LiabilityBatching.Split(candidates);
        logger.LogInformation(
            "Арбитр run={Run}: кандидатов={Count}, батчей={Batches}, модель={Model}",
            runId, candidates.Count, batches.Count, options.Model);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(await ArbitrateBatchAsync(
                duty, hypotheses, batch, runId, cancellationToken));
        }

        return new LlmArbiterResponse(results);
    }

    private async Task<IReadOnlyList<LlmArbiterItem>> ArbitrateBatchAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        IReadOnlyList<LiabilityCandidate> batch,
        Guid runId,
        CancellationToken cancellationToken)
    {
        string? feedback = null;
        for (var attempt = 1; attempt <= options.MaxRetries + 1; attempt++)
        {
            var userPrompt = LiabilityArbiterPrompts.BuildUserPrompt(
                duty, hypotheses, batch, feedback ?? string.Empty);
            var requestJson = JsonSerializer.Serialize(new
            {
                runId,
                attempt,
                model = options.Model,
                promptVersion = LiabilityMappingRules.PromptVersion,
                rulesVersion = LiabilityMappingRules.RulesVersion,
                configFingerprint = options.BuildConfigFingerprint(),
                nodeIds = batch.Select(item => item.LiabilityNodeId).ToArray(),
                userPrompt,
            });
            var stopwatch = Stopwatch.StartNew();
            string? responseJson = null;
            string? error = null;
            string validationResult = "invalid";

            try
            {
                responseJson = await completionClient.CompleteAsync(
                    LiabilityArbiterPrompts.SystemPrompt, userPrompt, cancellationToken);

                if (!LlmArbiterResponseParser.TryParse(responseJson, out var response, out error))
                {
                    throw new InvalidOperationException(error);
                }

                var missing = batch
                    .Select(item => item.LiabilityNodeId)
                    .Except(response.Results.Select(item => item.LiabilityNodeId), StringComparer.Ordinal)
                    .ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidOperationException(
                        "Арбитр не оценил кандидатов: " + string.Join(", ", missing));
                }

                validationResult = "valid";
                await WriteAuditAsync(
                    runId, duty, attempt, requestJson, responseJson, null,
                    stopwatch.ElapsedMilliseconds, validationResult, cancellationToken);
                return response.Results;
            }
            catch (OperationCanceledException)
            {
                await WriteAuditAsync(
                    runId, duty, attempt, requestJson, responseJson,
                    "Отменено вызывающей стороной", stopwatch.ElapsedMilliseconds,
                    "skipped", cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                logger.LogWarning(
                    "Арбитр run={Run}, батч {Attempt}/{Max}: {Error}",
                    runId, attempt, options.MaxRetries + 1, error);
                await WriteAuditAsync(
                    runId, duty, attempt, requestJson, responseJson, error,
                    stopwatch.ElapsedMilliseconds, "invalid", cancellationToken);

                if (attempt <= options.MaxRetries)
                {
                    var delay = options.RetryBaseDelayMs * (1 << (attempt - 1));
                    await Task.Delay(delay, cancellationToken);
                    feedback = Truncate(error, 800);
                }
            }
        }

        await WriteAuditAsync(
            runId, duty, options.MaxRetries + 1, string.Empty, null,
            "Попытки исчерпаны, батч передан в ручную проверку", 0,
            "manual_review", cancellationToken);

        return batch.Select(FallbackItem).ToArray();
    }

    private static LlmArbiterItem FallbackItem(LiabilityCandidate candidate)
    {
        var degree = candidate.NormType is LiabilityNormType.General or LiabilityNormType.Blanket
            ? MatchDegree.Blanket
            : MatchDegree.Partial;

        return new LlmArbiterItem(
            candidate.LiabilityNodeId,
            degree,
            0,
            [],
            "Требуется ручная проверка.",
            $"Автоматический арбитр исчерпал попытки для нормы {candidate.LiabilityNodeId}; " +
            "вывод не может считаться подтверждённым.",
            true);
    }

    private Task WriteAuditAsync(
        Guid runId,
        DutyInput duty,
        int attempt,
        string requestJson,
        string? responseJson,
        string? error,
        long durationMs,
        string validationResult,
        CancellationToken cancellationToken) =>
        store.WriteAuditAsync(new LlmAuditEntry(
            Guid.NewGuid(),
            runId,
            duty.Identifiers.DutyId,
            "arbiter",
            options.Model,
            options.Temperature,
            LiabilityMappingRules.PromptVersion,
            attempt,
            requestJson,
            responseJson,
            error,
            (int)Math.Min(durationMs, int.MaxValue),
            validationResult,
            DateTimeOffset.UtcNow), cancellationToken);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
