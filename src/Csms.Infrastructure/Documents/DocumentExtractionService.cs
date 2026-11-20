using System.Security.Cryptography;
using System.Text;
using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Оркестратор конвейера: текст → сегменты → Map → Validate → Reduce → версия.
/// dryRun считает всё, но ничего не сохраняет — можно проверить качество до
/// публикации и не засорять хранилище пробными прогонами.
/// </summary>
public sealed class DocumentExtractionService(
    DocumentFunctionsOptions options,
    DocumentTextExtractor extractor,
    DocumentSegmenter segmenter,
    DocumentFunctionExtractor map,
    DocumentFunctionValidator validator,
    DocumentFunctionReducer reducer,
    IDocumentFunctionStore store)
{
    public async Task<DocumentExtractionResponse> ExtractAsync(
        byte[] content,
        string fileName,
        bool dryRun,
        string? title = null,
        string? number = null,
        CancellationToken cancellationToken = default)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var text = await extractor.ExtractAsync(content, fileName, cancellationToken);
        var segments = segmenter.Segment(text);
        if (segments.Count == 0)
            throw new InvalidOperationException("В документе не найдено ни одного сегмента.");

        var meta = new DocumentFunctionDocumentMeta(
            sha256,
            fileName,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title,
            number,
            null,
            DocumentFunctionTaxonomy.Version,
            DocumentFunctionPromptVersions.Map);

        var mapped = await map.ExtractAsync(meta, segments, cancellationToken);
        var byId = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var validated = mapped
            .Select(item => validator.Validate(
                item,
                byId.TryGetValue(item.SegmentId, out var segment)
                    ? segment
                    : new DocumentSegment(item.SegmentId, item.SourcePath, [], null, 0, 0, string.Empty)))
            .Select(result => result.Extraction)
            .ToList();

        var reduced = reducer.Reduce(segments, validated);
        var version = MakeVersion(sha256, meta, options.Model);
        var failed = validated
            .Where(item => item.Status == "informational" && item.Report.QuotesRejected > 0)
            .Select(item => item.SegmentId)
            .ToList();
        // processed + failed == segments_total: informational-сегменты без
        // отклонённых цитат считаются нормально обработанными (в них просто
        // нет предписаний), а не проваленными.
        var report = reduced.Report with
        {
            FailedSegments = failed,
            Processed = validated.Count - failed.Count,
        };

        if (dryRun)
        {
            return new DocumentExtractionResponse(
                sha256, version, "dry_run", meta, reduced.Functions, report);
        }

        var set = new DocumentFunctionSet(
            sha256,
            version,
            meta,
            "llm:" + options.Model,
            reduced.Functions,
            report,
            DateTimeOffset.UtcNow);
        await store.PublishAsync(set, cancellationToken);
        return new DocumentExtractionResponse(
            sha256, version, "published", meta, reduced.Functions, report);
    }

    /// <summary>
    /// Версия набора: один и тот же файл, промпт, таксономия и модель дают
    /// одну версию. Смена модели автоматически означает новый прогон.
    /// </summary>
    private static string MakeVersion(
        string sha256, DocumentFunctionDocumentMeta meta, string model)
    {
        var raw = string.Join('|', sha256, meta.PromptVersion, meta.TaxonomyVersion, model);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
        return hash[..12];
    }
}
