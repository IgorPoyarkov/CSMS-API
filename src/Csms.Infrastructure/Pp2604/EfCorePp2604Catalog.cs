using Csms.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Csms.Infrastructure.Pp2604;

/// <summary>
/// Читает активную версию каталога 2604 из схемы legal. Таблицы созданы
/// raw SQL-миграцией, поэтому использован SqlQueryRaw, а не DbSet.
/// </summary>
public sealed class EfCorePp2604Catalog(
    IDbContextFactory<EisDbContext> contextFactory) : IPp2604Catalog
{
    public async Task<Pp2604Mapping?> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var documents = await context.Database.SqlQueryRaw<DocumentRow>(
            """
            SELECT version AS "Version", rules_version AS "RulesVersion",
                   eo_number AS "EoNumber", api_url AS "ApiUrl", pdf_url AS "PdfUrl",
                   pdf_sha256 AS "PdfSha256", document_title AS "DocumentTitle",
                   to_char(document_date, 'YYYY-MM-DD') AS "DocumentDate",
                   to_char(publish_date, 'YYYY-MM-DD') AS "PublishDate",
                   pages_count AS "PagesCount", appendix_pages AS "AppendixPages",
                   extraction_method AS "ExtractionMethod", imported_at AS "ImportedAt",
                   source_act AS "SourceAct", source_appendix AS "SourceAppendix",
                   source_portal AS "SourcePortal", source_classifier AS "SourceClassifier",
                   row_count AS "RowCount", link_count AS "LinkCount"
              FROM legal.pp2604_documents
             WHERE is_active
             ORDER BY imported_at DESC
             LIMIT 1
            """).ToListAsync(cancellationToken);

        var document = documents.FirstOrDefault();
        if (document is null)
        {
            return null;
        }

        var categories = await context.Database.SqlQueryRaw<CategoryRow>(
            """
            SELECT row_no AS "RowNo", category_id AS "CategoryId", label AS "Label",
                   appendix_name AS "AppendixName", min_price AS "MinPrice",
                   max_expenses AS "MaxExpenses", max_non_cost AS "MaxNonCost",
                   source_page AS "SourcePage"
              FROM legal.pp2604_categories
             WHERE document_version = @version
             ORDER BY row_no
            """, new NpgsqlParameter("version", document.Version))
            .ToListAsync(cancellationToken);

        var rules = await context.Database.SqlQueryRaw<RuleRow>(
            """
            SELECT okpd2_prefix AS "Prefix", category_id AS "CategoryId", row_no AS "RowNo",
                   basis AS "Basis", classifier_name AS "ClassifierName",
                   condition_text AS "Condition", needs_review AS "NeedsReview",
                   purpose_key AS "Purpose", source_text AS "Source", sort_order AS "SortOrder"
              FROM legal.pp2604_okpd2_links
             WHERE document_version = @version
             ORDER BY sort_order
            """, new NpgsqlParameter("version", document.Version))
            .ToListAsync(cancellationToken);

        var imported = document.ImportedAt.Kind == DateTimeKind.Utc
            ? document.ImportedAt
            : document.ImportedAt.ToUniversalTime();

        return new Pp2604Mapping(
            document.Version,
            document.RulesVersion,
            document.EoNumber,
            document.ApiUrl,
            document.PdfUrl,
            document.PdfSha256,
            document.DocumentTitle,
            document.DocumentDate,
            document.PublishDate,
            document.PagesCount,
            document.AppendixPages,
            document.ExtractionMethod,
            new DateTimeOffset(imported),
            new Pp2604SourceInfo(
                document.SourceAct,
                document.SourceAppendix,
                document.SourcePortal,
                document.EoNumber,
                document.AppendixPages,
                document.SourceClassifier),
            categories.Select(item => new Pp2604CategoryLimit(
                item.CategoryId, item.Label, item.RowNo, item.AppendixName,
                item.MinPrice, item.MaxExpenses, item.MaxNonCost, item.SourcePage)).ToArray(),
            rules.Select(item => new Pp2604Okpd2Link(
                item.Prefix, item.CategoryId, item.RowNo, item.Basis,
                item.ClassifierName, item.Condition, item.NeedsReview,
                item.Purpose, item.Source, item.SortOrder)).ToArray());
    }

#pragma warning disable CS8618 // Свойства заполняются EF из raw-запроса.
    private sealed class DocumentRow
    {
        public string Version { get; set; }
        public string RulesVersion { get; set; }
        public string EoNumber { get; set; }
        public string ApiUrl { get; set; }
        public string PdfUrl { get; set; }
        public string PdfSha256 { get; set; }
        public string DocumentTitle { get; set; }
        public string? DocumentDate { get; set; }
        public string? PublishDate { get; set; }
        public int? PagesCount { get; set; }
        public string AppendixPages { get; set; }
        public string ExtractionMethod { get; set; }
        public DateTime ImportedAt { get; set; }
        public string SourceAct { get; set; }
        public string SourceAppendix { get; set; }
        public string SourcePortal { get; set; }
        public string SourceClassifier { get; set; }
        public int RowCount { get; set; }
        public int LinkCount { get; set; }
    }

    private sealed class CategoryRow
    {
        public int RowNo { get; set; }
        public string CategoryId { get; set; }
        public string Label { get; set; }
        public string AppendixName { get; set; }
        public int MinPrice { get; set; }
        public int? MaxExpenses { get; set; }
        public int MaxNonCost { get; set; }
        public int SourcePage { get; set; }
    }

    private sealed class RuleRow
    {
        public string Prefix { get; set; }
        public string CategoryId { get; set; }
        public int RowNo { get; set; }
        public string Basis { get; set; }
        public string ClassifierName { get; set; }
        public string? Condition { get; set; }
        public bool NeedsReview { get; set; }
        public string? Purpose { get; set; }
        public string Source { get; set; }
        public int SortOrder { get; set; }
    }
#pragma warning restore CS8618
}
