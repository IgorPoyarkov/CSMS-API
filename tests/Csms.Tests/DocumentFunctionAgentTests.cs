using Csms.Application;
using Csms.Infrastructure.Documents;

namespace Csms.Tests;

/// <summary>
/// Тесты агента извлечения функций: главное — не потерять пункты и не
/// поверить галлюцинированной цитате. LLM в этих тестах не вызывается.
/// </summary>
public sealed class DocumentFunctionAgentTests
{
    [Fact]
    public void Сегментер_сохраняет_иерархию_и_не_теряет_пункты()
    {
        var text = string.Join("\n",
            "Приказ Минфина России от 31.07.2020 № 158н",
            "III. Функции контрактной службы",
            "3.1. При планировании закупок контрактная служба:",
            "3.1.1. Разрабатывает план-график и вносит в него изменения.",
            "3.1.2. Размещает план-график в единой информационной системе.");

        var segments = new DocumentSegmenter().Segment(text);

        // Заголовок раздела тоже даёт информационный сегмент — ни один символ
        // исходного текста не теряется, даже если в нём нет предписания.
        Assert.Equal(5, segments.Count);
        Assert.All(segments, segment => Assert.NotNull(segment.SourcePath));
        var clause = segments.Single(segment => segment.SegmentId == "p-3-1-1");
        Assert.Equal("III → 3.1 → 3.1.1", clause.SourcePath);
        Assert.Equal(3, clause.Ancestors.Count);
        Assert.Equal("3.1.1", clause.Ancestors[^1].Path);
        Assert.Contains("Разрабатывает план-график", clause.Text);

        var joined = string.Join(" ", segments.Select(segment => segment.Text));
        Assert.Contains("Размещает план-график", joined);
        Assert.Contains("Приказ Минфина", joined);
    }

    [Fact]
    public void Сегментер_режет_длинный_пункт_по_предложениям()
    {
        var text = "3.1.1. " + string.Concat(Enumerable.Repeat(
            "Контрактная служба разрабатывает план-график закупок. ", 60));

        var segments = new DocumentSegmenter().Segment(text);

        Assert.True(segments.Count > 1);
        Assert.All(segments, segment => Assert.StartsWith("p-3-1-1#", segment.SegmentId));
        Assert.Equal(
            DocumentFunctionNormalizer.Compare(text),
            DocumentFunctionNormalizer.Compare(string.Join(" ", segments.Select(s => s.Text))));
    }

    [Fact]
    public void Сегментер_делает_уникальные_id_для_повторяющихся_подпунктов()
    {
        var text = string.Join("\n",
            "3.1.1. Разрабатывает план-график.",
            "а) Утверждает график.",
            "3.1.2. Размещает план-график.",
            "а) Направляет уведомление.");

        var segments = new DocumentSegmenter().Segment(text);
        var ids = segments.Select(segment => segment.SegmentId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Валидатор_отбрасывает_цитату_которой_нет_в_сегменте()
    {
        var segment = Segment("3.1.1", "Разрабатывает план-график закупок.");
        var extraction = Extraction(segment, "Разработать", "План-график",
            "Направляет отчёт в Минфин");

        var result = new DocumentFunctionValidator().Validate(extraction, segment);

        Assert.Equal(1, result.QuotesRejected);
        Assert.Empty(result.Extraction.ExtractedFunctions);
        Assert.Equal("informational", result.Extraction.Status);
    }

    [Fact]
    public void Валидатор_не_выдумывает_срок_и_помечает_проверку()
    {
        var segment = Segment("3.1.1", "Разрабатывает план-график закупок.");
        var extraction = Extraction(segment, "Разработать", "План-график",
            "Разрабатывает план-график закупок.") with
        {
            ExtractedFunctions =
            [
                Function() with
                {
                    Subtasks =
                    [
                        Subtask("Разработать", "План-график",
                            "Разрабатывает план-график закупок.") with
                        {
                            DeadlineRule = "в течение 5 рабочих дней",
                        },
                    ],
                },
            ],
        };

        var result = new DocumentFunctionValidator().Validate(extraction, segment);

        var subtask = Assert.Single(
            Assert.Single(result.Extraction.ExtractedFunctions).Subtasks);
        Assert.Null(subtask.DeadlineRule);
        Assert.True(subtask.NeedsReview);
        Assert.Equal(0, result.QuotesRejected);
    }

    [Fact]
    public void Парсер_возвращает_все_сегменты_батча_даже_если_модель_пропустила_один()
    {
        var first = Segment("3.1.1", "Разрабатывает план-график.");
        var second = Segment("3.1.2", "Размещает извещение.");
        var json = """
            {"results":[{"segment_id":"p-3-1-1","status":"extracted","extracted_functions":[]}]}
            """;

        var parsed = DocumentFunctionResponseParser.Parse(json, [first, second]);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("informational", parsed.Single(item => item.SegmentId == "p-3-1-2").Status);
        Assert.DoesNotContain(parsed, item => item.SegmentId == "invented");
    }

    [Fact]
    public void Редуксер_склеивает_дубли_и_сохраняет_все_ссылки()
    {
        var first = Segment("3.1.1", "Разрабатывает проект контракта.");
        var second = Segment("3.4.1", "Разрабатывает проект контракта.");
        var validated = new[]
        {
            Extraction(first, "Разработать", "Проект контракта",
                "Разрабатывает проект контракта."),
            Extraction(second, "Разработать", "Проект контракта",
                "Разрабатывает проект контракта."),
        };

        var result = new DocumentFunctionReducer().Reduce([first, second], validated);

        var function = Assert.Single(result.Functions);
        Assert.Equal(2, function.SourcePaths.Count);
        Assert.Equal(DocumentFunctionReviewStatuses.NeedsReview, function.ReviewStatus);
        Assert.Equal(2, result.Report.CandidatesBefore);
        Assert.Equal(1, result.Report.FunctionsAfter);
        Assert.Equal(1, result.Report.DuplicatesMerged);
        Assert.Empty(result.Report.UncoveredSegments);
    }

    [Fact]
    public async Task Хранилище_не_перезаписывает_версии_и_подтверждает_по_id()
    {
        var store = new InMemoryDocumentFunctionStore();
        var function = FunctionItem();
        var first = Set("v1", function);
        var second = Set("v2", function);

        await store.PublishAsync(first);
        await store.PublishAsync(second);
        await store.PublishAsync(Set("v1", function with { Id = Guid.NewGuid() }));

        Assert.Equal(2, (await store.GetVersionsAsync(first.Sha256)).Count);
        Assert.Equal("v2", (await store.GetActiveAsync(first.Sha256))!.Version);

        var verified = await store.VerifyAsync(
            function.Id, DocumentFunctionReviewStatuses.Approved, "проверено вручную");
        Assert.NotNull(verified);
        Assert.Equal(DocumentFunctionReviewStatuses.Approved, verified.ReviewStatus);
        Assert.Equal("проверено вручную", verified.ReviewComment);
        Assert.Equal(
            DocumentFunctionReviewStatuses.Approved,
            Assert.Single((await store.GetActiveAsync(first.Sha256))!.Functions).ReviewStatus);
    }

    private static DocumentSegment Segment(string path, string text) =>
        new("p-" + path.Replace('.', '-'), path, [], 1, 0, text.Length, text);

    private static ExtractedDocumentSubtask Subtask(
        string verb, string target, string quote) =>
        new("F1.1", verb, target, "Атомарная задача", null, null, quote, false, 0.9);

    private static ExtractedDocumentFunction Function() =>
        new("F1", "Работа с проектом контракта",
            DocumentFunctionTaxonomy.ContractConclusion, 0.9, null,
            "Контрактная служба", DocumentFunctionObligations.Mandatory, true,
            [Subtask("Разработать", "Проект контракта", "Разрабатывает проект контракта.")]);

    private static DocumentFunctionItem FunctionItem() =>
        new(Guid.NewGuid(), "verb|object", "Работа с проектом контракта",
            DocumentFunctionTaxonomy.ContractConclusion, null,
            "Контрактная служба", DocumentFunctionObligations.Mandatory,
            "один сегмент", ["п. 3.1.1"],
            [
                new DocumentFunctionSubtaskItem(
                    Guid.NewGuid(), "verb|object", "Разработать", "Проект контракта",
                    "Атомарная задача", null, null,
                    "Разрабатывает проект контракта.", ["п. 3.1.1"], false, 0.9),
            ],
            DocumentFunctionReviewStatuses.NeedsReview, null);

    private static DocumentSegmentExtraction Extraction(
        DocumentSegment segment, string verb, string target, string quote) =>
        new(segment.SegmentId, segment.SourcePath, "extracted",
            [Function() with { Subtasks = [Subtask(verb, target, quote)] }],
            new DocumentSegmentReport(1, 1, false, 1, 0, 0));

    private const string DocumentSha = "sha-order158n";

    private static DocumentFunctionSet Set(string version, DocumentFunctionItem function) =>
        new(DocumentSha, version,
            new DocumentFunctionDocumentMeta(
                DocumentSha, "order158n.txt", "Приказ № 158н", "158н",
                null, DocumentFunctionTaxonomy.Version, DocumentFunctionPromptVersions.Map),
            "qwen2.5:14b-instruct-q4_K_M",
            [function],
            new DocumentExtractionReport(0, 0, [], [], 1, 1, 0, 0, 0),
            DateTimeOffset.UtcNow);
}
