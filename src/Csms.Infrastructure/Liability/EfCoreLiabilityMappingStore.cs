using System.Text.Json;
using Csms.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// PostgreSQL-хранилище схемы legal. Таблицы создаются миграцией с raw SQL:
/// у vector и tsvector нет CLR-типов в базовом провайдере Npgsql, поэтому
/// чтение и запись идут через параметры с явным приведением, а не через DbSet.
/// </summary>
public sealed class EfCoreLiabilityMappingStore(
    IDbContextFactory<EisDbContext> contextFactory) : ILiabilityMappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> HasCompletedRunAsync(
        Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var count = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int FROM legal.duty_liability_mappings WHERE run_id = @runId",
                new NpgsqlParameter("runId", runId))
            .SingleAsync(cancellationToken);
        return count > 0;
    }

    public async Task SaveCandidatesAsync(
        Guid runId,
        string dutyId,
        IReadOnlyList<LiabilityCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM legal.mapping_candidates WHERE run_id = @runId AND duty_id = @dutyId",
            new NpgsqlParameter("runId", runId),
            new NpgsqlParameter("dutyId", dutyId),
            cancellationToken);

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO legal.mapping_candidates
                    (id, run_id, duty_id, liability_node_id, source, search_score, rank, chunk_id, payload)
                VALUES
                    (@id, @runId, @dutyId, @nodeId, @source, @score, @rank,
                     CAST(@chunkId AS uuid), CAST(@payload AS jsonb))
                """,
                new NpgsqlParameter("id", Guid.NewGuid()),
                new NpgsqlParameter("runId", runId),
                new NpgsqlParameter("dutyId", dutyId),
                new NpgsqlParameter("nodeId", candidate.LiabilityNodeId),
                new NpgsqlParameter("source", SourceToString(candidate.Source)),
                new NpgsqlParameter("score", candidate.SearchScore),
                new NpgsqlParameter("rank", index),
                new NpgsqlParameter("chunkId", (object?)candidate.ChunkId ?? DBNull.Value),
                new NpgsqlParameter("payload", JsonSerializer.Serialize(candidate, JsonOptions)),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LiabilityCandidate>> GetCandidatesAsync(
        Guid runId,
        string dutyId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<CandidateRow>(
                """
                SELECT payload::text AS "Payload"
                FROM legal.mapping_candidates
                WHERE run_id = @runId AND duty_id = @dutyId
                ORDER BY rank
                """,
                new NpgsqlParameter("runId", runId),
                new NpgsqlParameter("dutyId", dutyId))
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => JsonSerializer.Deserialize<LiabilityCandidate>(row.Payload, JsonOptions))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    public async Task SaveMappingsAsync(
        Guid runId,
        LiabilityMappingResult result,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Старые связи с узлами, которых нет в новом пуле, удаляем: иначе
        // результат содержал бы нормы вне текущего пула кандидатов.
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM legal.duty_liability_mappings
            WHERE duty_id = @dutyId
              AND NOT (liability_node_id = ANY(@nodeIds))
            """,
            new NpgsqlParameter("dutyId", result.DutyId),
            new NpgsqlParameter("nodeIds", result.MatchedNorms
                .Select(match => match.LiabilityNodeId).ToArray()),
            cancellationToken);

        var hypothesesJson = JsonSerializer.Serialize(result.Hypotheses, JsonOptions);
        foreach (var match in result.MatchedNorms)
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO legal.duty_liability_mappings
                    (id, run_id, duty_id, liability_node_id, match_degree, probability_score,
                     qualifying_conditions, sanction_summary, legal_reasoning,
                     requires_human_review, hypotheses, model_version, prompt_version,
                     audit_id, calculated_at)
                VALUES
                    (@id, @runId, @dutyId, @nodeId, @degree, @score,
                     CAST(@conditions AS jsonb), @sanction, @reasoning,
                     @review, CAST(@hypotheses AS jsonb), @model, @prompt,
                     NULL, @calculatedAt)
                ON CONFLICT (duty_id, liability_node_id) DO UPDATE SET
                    run_id = EXCLUDED.run_id,
                    match_degree = EXCLUDED.match_degree,
                    probability_score = EXCLUDED.probability_score,
                    qualifying_conditions = EXCLUDED.qualifying_conditions,
                    sanction_summary = EXCLUDED.sanction_summary,
                    legal_reasoning = EXCLUDED.legal_reasoning,
                    requires_human_review = EXCLUDED.requires_human_review,
                    hypotheses = EXCLUDED.hypotheses,
                    model_version = EXCLUDED.model_version,
                    prompt_version = EXCLUDED.prompt_version,
                    calculated_at = EXCLUDED.calculated_at
                """,
                new NpgsqlParameter("id", Guid.NewGuid()),
                new NpgsqlParameter("runId", runId),
                new NpgsqlParameter("dutyId", result.DutyId),
                new NpgsqlParameter("nodeId", match.LiabilityNodeId),
                new NpgsqlParameter("degree", match.MatchDegree.ToString().ToLowerInvariant()),
                new NpgsqlParameter("score", match.ProbabilityScore),
                new NpgsqlParameter("conditions", JsonSerializer.Serialize(match.QualifyingConditions, JsonOptions)),
                new NpgsqlParameter("sanction", match.SanctionSummary),
                new NpgsqlParameter("reasoning", match.LegalReasoning),
                new NpgsqlParameter("review", match.RequiresHumanReview),
                new NpgsqlParameter("hypotheses", hypothesesJson),
                new NpgsqlParameter("model", result.ModelVersion),
                new NpgsqlParameter("prompt", result.PromptVersion),
                new NpgsqlParameter("calculatedAt", result.CalculatedAt.UtcDateTime),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LiabilityMappingResult?> GetResultAsync(
        Guid runId,
        string dutyId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<MappingRow>(
                """
                SELECT duty_id AS "DutyId", run_id AS "RunId",
                       hypotheses::text AS "HypothesesJson",
                       model_version AS "ModelVersion", prompt_version AS "PromptVersion",
                       calculated_at AS "CalculatedAt",
                       liability_node_id AS "LiabilityNodeId", norm_reference AS "NormReference",
                       match_degree AS "MatchDegree",
                       probability_score::double precision AS "ProbabilityScore",
                       qualifying_conditions::text AS "ConditionsJson",
                       sanction_summary AS "SanctionSummary",
                       legal_reasoning AS "LegalReasoning",
                       requires_human_review AS "RequiresHumanReview"
                FROM legal.duty_liability_mappings
                WHERE run_id = @runId AND duty_id = @dutyId
                ORDER BY probability_score DESC, liability_node_id
                """,
                new NpgsqlParameter("runId", runId),
                new NpgsqlParameter("dutyId", dutyId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var matches = rows.Select(row => new MatchedLiability(
                row.LiabilityNodeId,
                row.NormReference,
                Enum.TryParse<MatchDegree>(row.MatchDegree, ignoreCase: true, out var degree)
                    ? degree
                    : MatchDegree.Partial,
                row.ProbabilityScore,
                JsonSerializer.Deserialize<string[]>(row.ConditionsJson, JsonOptions) ?? [],
                row.SanctionSummary,
                row.LegalReasoning,
                row.RequiresHumanReview))
            .ToArray();

        var candidateCount = await context.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int FROM legal.mapping_candidates
                WHERE run_id = @runId AND duty_id = @dutyId
                """,
                new NpgsqlParameter("runId", runId),
                new NpgsqlParameter("dutyId", dutyId))
            .SingleAsync(cancellationToken);

        var first = rows[0];
        return new LiabilityMappingResult(
            dutyId,
            runId,
            JsonSerializer.Deserialize<ViolationHypothesis[]>(first.HypothesesJson, JsonOptions) ?? [],
            matches,
            candidateCount,
            first.ModelVersion,
            first.PromptVersion,
            first.CalculatedAt,
            matches.Any(match => match.RequiresHumanReview));
    }

    public async Task<LiabilityMappingResult?> GetLatestByDutyAsync(
        string dutyId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var runId = await context.Database.SqlQueryRaw<string>(
                """
                SELECT run_id::text FROM legal.duty_liability_mappings
                WHERE duty_id = @dutyId
                ORDER BY calculated_at DESC
                LIMIT 1
                """,
                new NpgsqlParameter("dutyId", dutyId))
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(runId) || !Guid.TryParse(runId, out var parsed)
            ? null
            : await GetResultAsync(parsed, dutyId, cancellationToken);
    }

    public async Task WriteAuditAsync(
        LlmAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO legal.llm_audit_log
                (id, run_id, duty_id, stage, model, temperature, prompt_version, attempt,
                 request_json, response_json, error_text, duration_ms, validation_result, created_at)
            VALUES
                (@id, @runId, @dutyId, @stage, @model, CAST(@temperature AS numeric(3,2)),
                 @promptVersion, @attempt, CAST(@request AS jsonb), CAST(@response AS jsonb),
                 @error, @duration, @validation, @createdAt)
            """,
            new NpgsqlParameter("id", entry.Id),
            new NpgsqlParameter("runId", entry.RunId),
            new NpgsqlParameter("dutyId", entry.DutyId),
            new NpgsqlParameter("stage", entry.Stage),
            new NpgsqlParameter("model", entry.Model),
            new NpgsqlParameter("temperature", entry.Temperature),
            new NpgsqlParameter("promptVersion", entry.PromptVersion),
            new NpgsqlParameter("attempt", entry.Attempt),
            new NpgsqlParameter("request", entry.RequestJson),
            new NpgsqlParameter("response", (object?)entry.ResponseJson ?? DBNull.Value),
            new NpgsqlParameter("error", (object?)entry.ErrorText ?? DBNull.Value),
            new NpgsqlParameter("duration", entry.DurationMs),
            new NpgsqlParameter("validation", entry.ValidationResult),
            new NpgsqlParameter("createdAt", entry.CreatedAt.UtcDateTime),
            cancellationToken);
    }

    private static string SourceToString(CandidateSource source) => source switch
    {
        CandidateSource.Vector => "vector",
        CandidateSource.FullText => "fulltext",
        CandidateSource.Umbrella => "umbrella",
        _ => "merged",
    };

    private sealed class CandidateRow
    {
        public string Payload { get; set; } = string.Empty;
    }

    private sealed class MappingRow
    {
        public string DutyId { get; set; } = string.Empty;
        public Guid RunId { get; set; }
        public string HypothesesJson { get; set; } = "[]";
        public string ModelVersion { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public DateTimeOffset CalculatedAt { get; set; }
        public string LiabilityNodeId { get; set; } = string.Empty;
        public string NormReference { get; set; } = string.Empty;
        public string MatchDegree { get; set; } = string.Empty;
        public double ProbabilityScore { get; set; }
        public string ConditionsJson { get; set; } = "[]";
        public string SanctionSummary { get; set; } = string.Empty;
        public string LegalReasoning { get; set; } = string.Empty;
        public bool RequiresHumanReview { get; set; }
    }
}
