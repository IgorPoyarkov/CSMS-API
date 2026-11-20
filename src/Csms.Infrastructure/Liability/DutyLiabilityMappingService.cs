using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Оркестратор конвейера. Ключ запуска считается после retrieval: один и тот же
/// пул кандидатов + версии модели/промпта дают один run_id, поэтому повтор
/// возвращает сохранённый результат, не дёргая арбитра и не создавая дубли.
/// </summary>
public sealed class DutyLiabilityMappingService(
    IViolationHypothesisGenerator hypothesisGenerator,
    ILiabilityRetriever retriever,
    ILlmArbiter arbiter,
    IMappingValidator validator,
    ILiabilityMappingStore store,
    LiabilityMappingOptions options,
    ILogger<DutyLiabilityMappingService> logger) : IDutyLiabilityMappingService
{
    public async Task<LiabilityMappingResult> MapAsync(
        DutyInput duty, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duty);
        if (string.IsNullOrWhiteSpace(duty.Identifiers.DutyId))
        {
            throw new ArgumentException("Identifiers.DutyId обязателен.", nameof(duty));
        }

        var hypotheses = hypothesisGenerator.Generate(duty);
        var candidates = await retriever.RetrieveAsync(duty, hypotheses, cancellationToken);
        var runId = LiabilityMappingRules.BuildRunId(
            duty, candidates, options.Model, options.BuildConfigFingerprint());

        // Пустой результат не оставляет строк в duty_liability_mappings, поэтому
        // одной проверки GetResultAsync недостаточно: completed-run маркер в
        // mapping_candidates фиксирует даже успешный прогон без совпадений.
        if (await store.HasCompletedRunAsync(runId, cancellationToken))
        {
            var completed = await store.GetResultAsync(runId, duty.Identifiers.DutyId, cancellationToken);
            if (completed is not null)
            {
                logger.LogInformation(
                    "run={Run} уже завершён, возвращаем сохранённый результат ({Count} норм)",
                    runId, completed.MatchedNorms.Count);
                return completed;
            }

            return new LiabilityMappingResult(
                duty.Identifiers.DutyId,
                runId,
                hypotheses,
                [],
                candidates.Count,
                options.Model,
                LiabilityMappingRules.PromptVersion,
                DateTimeOffset.UtcNow,
                true);
        }

        var cached = await store.GetResultAsync(runId, duty.Identifiers.DutyId, cancellationToken);
        if (cached is not null)
        {
            logger.LogInformation(
                "run={Run} уже завершён, возвращаем сохранённый результат ({Count} норм)",
                runId, cached.MatchedNorms.Count);
            return cached;
        }

        await store.SaveCandidatesAsync(runId, duty.Identifiers.DutyId, candidates, cancellationToken);

        var response = await arbiter.ArbitrateAsync(
            duty, hypotheses, candidates, runId, cancellationToken);
        var outcome = validator.Validate(response, candidates);

        foreach (var violation in outcome.Violations)
        {
            logger.LogWarning("run={Run}: {Violation}", runId, violation);
        }

        // Пустой пул — корректный результат: арбитр не вызывается, а пустой
        // список связей честно фиксируется в хранилище.
        var result = new LiabilityMappingResult(
            duty.Identifiers.DutyId,
            runId,
            hypotheses,
            outcome.Accepted,
            candidates.Count,
            options.Model,
            LiabilityMappingRules.PromptVersion,
            DateTimeOffset.UtcNow,
            outcome.Accepted.Any(match => match.RequiresHumanReview) ||
            outcome.NeedsManualReviewFallback);

        await store.SaveMappingsAsync(runId, result, cancellationToken);
        logger.LogInformation(
            "run={Run}: сопоставлено {Count} норм, на проверке {Review}",
            runId,
            result.MatchedNorms.Count,
            result.MatchedNorms.Count(match => match.RequiresHumanReview));
        return result;
    }

    public Task<LiabilityMappingResult?> GetAsync(
        string dutyId, CancellationToken cancellationToken = default) =>
        store.GetLatestByDutyAsync(dutyId, cancellationToken);
}
