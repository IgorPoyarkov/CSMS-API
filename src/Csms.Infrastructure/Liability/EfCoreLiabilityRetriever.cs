using System.Text;
using System.Text.Json;
using Csms.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Этап 2: гибридный recall. Вектор ищет по смыслу, tsvector — по реквизитам
/// и точным словам, а общие/бланкетные нормы добавляются принудительно,
/// чтобы маленькая модель не «потеряла» umbrella-состав.
/// </summary>
public sealed class EfCoreLiabilityRetriever(
    IDbContextFactory<EisDbContext> contextFactory,
    ILiabilityEmbeddingService embeddingService,
    ILogger<EfCoreLiabilityRetriever> logger) : ILiabilityRetriever
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<LiabilityCandidate>> RetrieveAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duty);

        var queryText = BuildQueryText(duty, hypotheses);
        var hits = new Dictionary<Guid, RankHit>();

        try
        {
            var embedding = await embeddingService.EmbedAsync(queryText, cancellationToken);
            if (embedding.Length != 768)
            {
                logger.LogWarning(
                    "Эмбеддинг размерности {Actual}, ожидалось 768: векторный поиск пропущен",
                    embedding.Length);
            }
            else
            {
                var vectorHits = await RunVectorSearchAsync(
                    duty.LiabilityActCode, OllamaEmbeddingService.ToPgVector(embedding), cancellationToken);
                Merge(hits, vectorHits, CandidateSource.Vector);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Модель эмбеддингов может быть не загружена. Полнотекстовый поиск
            // и общие нормы обязаны работать и в этом случае.
            logger.LogWarning(exception, "Векторный поиск недоступен, используется FTS+umbrella");
        }

        var textHits = await RunFullTextSearchAsync(
            duty.LiabilityActCode, queryText, cancellationToken);
        Merge(hits, textHits, CandidateSource.FullText);

        var umbrellaHits = await RunUmbrellaSearchAsync(duty.LiabilityActCode, cancellationToken);
        Merge(hits, umbrellaHits, CandidateSource.Umbrella);

        // FTS по всей формулировке обязанности превращается в длинный AND и
        // теряет узкие нормы (например, 7.30.1 про план-график). Доменный
        // поиск по специальным нормам — детерминированный способ вернуть их в пул.
        var domainHits = await RunDomainSearchAsync(
            duty.LiabilityActCode, queryText, cancellationToken);
        Merge(hits, domainHits, CandidateSource.FullText);

        var norms = await LoadNormsAsync(hits.Keys, cancellationToken);
        var candidates = hits
            .Where(pair => norms.ContainsKey(pair.Key))
            .Select(pair => ToCandidate(norms[pair.Key], pair.Value, queryText))
            .OrderByDescending(candidate => candidate.SearchScore)
            .ThenBy(candidate => candidate.LiabilityNodeId, StringComparer.Ordinal)
            .Take(LiabilityMappingRules.MaxCandidates)
            .ToArray();

        logger.LogInformation(
            "Пул кандидатов duty={Duty}, act={Act}: {Count} норм (вектор={Vector}, FTS={Fts}, umbrella={Umbrella})",
            duty.Identifiers.DutyId, duty.LiabilityActCode, candidates.Length,
            hits.Values.Count(hit => hit.VectorScore is not null),
            hits.Values.Count(hit => hit.TextScore is not null),
            hits.Values.Count(hit => hit.UmbrellaScore is not null));

        if (candidates.Length < LiabilityMappingRules.PreferredCandidates)
        {
            logger.LogWarning(
                "Пул кандидатов duty={Duty}: {Count} норм меньше желаемых {Preferred}",
                duty.Identifiers.DutyId, candidates.Length,
                LiabilityMappingRules.PreferredCandidates);
        }

        return candidates;
    }

    private async Task<IReadOnlyList<RankHit>> RunVectorSearchAsync(
        string actCode, string vector, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<RankRow>(
                """
                SELECT n.id AS "Id", c.id AS "ChunkId",
                       GREATEST(0.0, 1.0 - (c.embedding <=> CAST(@vector AS vector(768)))) AS "Score"
                FROM legal.liability_norm_chunks c
                JOIN legal.liability_norms n ON n.id = c.norm_id
                WHERE n.act_code = @actCode AND n.status = 'in_force'
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@markers) AS marker
                      WHERE n.text ILIKE '%' || marker || '%')
                ORDER BY c.embedding <=> CAST(@vector AS vector(768))
                LIMIT @limit
                """,
                new NpgsqlParameter("vector", vector),
                new NpgsqlParameter("actCode", actCode),
                new NpgsqlParameter("markers", LiabilityMappingRules.NonLiabilityMarkers),
                new NpgsqlParameter("limit", LiabilityMappingRules.VectorCandidateLimit))
            .ToListAsync(cancellationToken);
        return rows.Select(row => new RankHit(row.Id, row.ChunkId, VectorScore: row.Score)).ToArray();
    }

    private async Task<IReadOnlyList<RankHit>> RunFullTextSearchAsync(
        string actCode, string query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<RankRow>(
                """
                SELECT n.id AS "Id", NULL::uuid AS "ChunkId",
                       ts_rank_cd(c.search_tsv, websearch_to_tsquery('russian', @query)) AS "Score"
                FROM legal.liability_norm_chunks c
                JOIN legal.liability_norms n ON n.id = c.norm_id
                WHERE n.act_code = @actCode AND n.status = 'in_force'
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@markers) AS marker
                      WHERE n.text ILIKE '%' || marker || '%')
                  AND c.search_tsv @@ websearch_to_tsquery('russian', @query)
                ORDER BY ts_rank_cd(c.search_tsv, websearch_to_tsquery('russian', @query)) DESC
                LIMIT @limit
                """,
                new NpgsqlParameter("actCode", actCode),
                new NpgsqlParameter("query", query),
                new NpgsqlParameter("markers", LiabilityMappingRules.NonLiabilityMarkers),
                new NpgsqlParameter("limit", LiabilityMappingRules.FullTextCandidateLimit))
            .ToListAsync(cancellationToken);
        return rows.Select(row => new RankHit(row.Id, row.ChunkId, TextScore: row.Score)).ToArray();
    }

    private async Task<IReadOnlyList<RankHit>> RunUmbrellaSearchAsync(
        string actCode, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<RankRow>(
                """
                SELECT id AS "Id", NULL::uuid AS "ChunkId", 0.35::double precision AS "Score"
                FROM legal.liability_norms
                WHERE act_code = @actCode AND status = 'in_force'
                  AND norm_type IN ('general', 'blanket')
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@markers) AS marker
                      WHERE text ILIKE '%' || marker || '%')
                ORDER BY updated_at DESC
                LIMIT @limit
                """,
                new NpgsqlParameter("actCode", actCode),
                new NpgsqlParameter("markers", LiabilityMappingRules.NonLiabilityMarkers),
                new NpgsqlParameter("limit", LiabilityMappingRules.UmbrellaCandidateLimit))
            .ToListAsync(cancellationToken);
        return rows.Select(row => new RankHit(row.Id, row.ChunkId, UmbrellaScore: row.Score)).ToArray();
    }

    /// <summary>
    /// Ищет специальные нормы по лексике обязанности. Если речь о план-графике,
    /// дополнительно требуется упоминание план-графика в тексте нормы.
    /// </summary>
    private async Task<IReadOnlyList<RankHit>> RunDomainSearchAsync(
        string actCode, string query, CancellationToken cancellationToken)
    {
        var markers = LiabilityMappingRules.ProcurementDomainMarkers
            .Where(marker => query.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (markers.Length == 0)
        {
            return [];
        }

        var required = LiabilityMappingRules.PlanSchedulePhrases
            .Where(phrase => query.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Database.SqlQueryRaw<RankRow>(
                """
                SELECT n.id AS "Id", NULL::uuid AS "ChunkId",
                       0.5
                       + 0.03 * (SELECT count(*) FROM unnest(@markers) AS m
                                 WHERE n.text ILIKE '%' || m || '%')
                       + 0.1 * (SELECT count(*) FROM unnest(@required) AS r
                                WHERE n.text ILIKE '%' || r || '%') AS "Score"
                FROM legal.liability_norms n
                WHERE n.act_code = @actCode AND n.status = 'in_force'
                  AND n.norm_type = 'special'
                  AND EXISTS (SELECT 1 FROM unnest(@markers) AS m
                              WHERE n.text ILIKE '%' || m || '%')
                  AND (cardinality(@required) = 0 OR EXISTS (
                      SELECT 1 FROM unnest(@required) AS r
                      WHERE n.text ILIKE '%' || r || '%'))
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@excluded) AS marker
                      WHERE n.text ILIKE '%' || marker || '%')
                ORDER BY "Score" DESC, n.liability_node_id
                LIMIT @limit
                """,
                new NpgsqlParameter("actCode", actCode),
                new NpgsqlParameter("markers", markers),
                new NpgsqlParameter("required", required),
                new NpgsqlParameter("excluded", LiabilityMappingRules.NonLiabilityMarkers),
                new NpgsqlParameter("limit", LiabilityMappingRules.DomainCandidateLimit))
            .ToListAsync(cancellationToken);
        return rows.Select(row => new RankHit(row.Id, row.ChunkId, TextScore: row.Score)).ToArray();
    }

    private async Task<Dictionary<Guid, NormRow>> LoadNormsAsync(
        IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var allIds = ids.Distinct().ToArray();
        if (allIds.Length == 0)
        {
            return [];
        }

        var parameters = allIds
            .Select((id, index) => new NpgsqlParameter($"id{index}", id))
            .Cast<object>()
            .ToArray();
        var placeholders = string.Join(", ", allIds.Select((_, index) => $"@id{index}"));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Плейсхолдеры строятся только из индексов массива Guid, пользовательские
        // данные уходят параметрами — SQL-инъекции здесь невозможны.
#pragma warning disable EF1002
        var rows = await context.Database.SqlQueryRaw<NormRow>(
                $"""
                 SELECT id AS "Id", liability_node_id AS "LiabilityNodeId",
                        norm_reference AS "NormReference", text AS "Text",
                        act_code AS "ActCode", act_version AS "ActVersion",
                        norm_type AS "NormType", status AS "Status",
                        edition_date AS "EditionDate", metadata::text AS "MetadataJson"
                 FROM legal.liability_norms
                 WHERE id IN ({placeholders})
                 """,
                parameters)
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002
        return rows.ToDictionary(row => row.Id);
    }

    private static void Merge(
        Dictionary<Guid, RankHit> target,
        IReadOnlyList<RankHit> hits,
        CandidateSource source)
    {
        foreach (var hit in hits)
        {
            if (!target.TryGetValue(hit.Id, out var existing))
            {
                target[hit.Id] = hit;
                continue;
            }

            target[hit.Id] = existing with
            {
                VectorScore = Math.Max(existing.VectorScore ?? 0, hit.VectorScore ?? 0),
                TextScore = Math.Max(existing.TextScore ?? 0, hit.TextScore ?? 0),
                UmbrellaScore = Math.Max(existing.UmbrellaScore ?? 0, hit.UmbrellaScore ?? 0),
                ChunkId = hit.ChunkId ?? existing.ChunkId,
            };
        }
    }

    private static LiabilityCandidate ToCandidate(NormRow norm, RankHit hit, string dutyQuery)
    {
        // Взвешенная сумма вместо max: норма, найденная и вектором, и FTS,
        // получает законно более высокий ранг, а umbrella не вытесняет специфичные.
        var score = Math.Clamp(
            (hit.VectorScore ?? 0) * LiabilityMappingRules.VectorSearchWeight +
            (hit.TextScore ?? 0) * LiabilityMappingRules.FullTextSearchWeight +
            (hit.UmbrellaScore ?? 0) * LiabilityMappingRules.UmbrellaSearchWeight,
            0,
            1);

        // Доменная лексика обязанности поднимает специальную норму выше общих.
        // Это детерминированный реранкинг, а не «ещё один LLM-судья».
        var dutyMatches = LiabilityMappingRules.ProcurementDomainMarkers
            .Any(marker => dutyQuery.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var normMatches = LiabilityMappingRules.ProcurementDomainMarkers
            .Any(marker => norm.Text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (dutyMatches && normMatches && norm.NormType.Equals("special", StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Clamp(score * LiabilityMappingRules.DomainSpecialBoost, 0, 1);
        }

        return new LiabilityCandidate(
            norm.LiabilityNodeId,
            norm.NormReference,
            norm.Text,
            norm.ActCode,
            norm.ActVersion,
            ParseNormType(norm.NormType),
            norm.Status,
            norm.EditionDate,
            ParseMetadata(norm.MetadataJson),
            CandidateSource.Merged,
            score,
            hit.ChunkId?.ToString());
    }

    private static LiabilityNormType ParseNormType(string value) =>
        Enum.TryParse<LiabilityNormType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LiabilityNormType.Other;

    private static IReadOnlyDictionary<string, string> ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.ToString(),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string BuildQueryText(
        DutyInput duty, IReadOnlyList<ViolationHypothesis> hypotheses)
    {
        var parts = new List<string>
        {
            duty.Statement,
            duty.Object,
            duty.Subject,
            duty.Deadline ?? string.Empty,
            duty.Condition ?? string.Empty,
        };
        parts.AddRange(hypotheses.Select(hypothesis => hypothesis.Text));
        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private sealed record RankHit(
        Guid Id,
        Guid? ChunkId,
        double? VectorScore = null,
        double? TextScore = null,
        double? UmbrellaScore = null);

    private sealed class RankRow
    {
        public Guid Id { get; set; }
        public Guid? ChunkId { get; set; }
        public double Score { get; set; }
    }

    private sealed class NormRow
    {
        public Guid Id { get; set; }
        public string LiabilityNodeId { get; set; } = string.Empty;
        public string NormReference { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string ActCode { get; set; } = string.Empty;
        public string ActVersion { get; set; } = string.Empty;
        public string NormType { get; set; } = "special";
        public string Status { get; set; } = "in_force";
        public DateOnly? EditionDate { get; set; }
        public string MetadataJson { get; set; } = "{}";
    }
}
