using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Csms.Application;
using Csms.Infrastructure.Liability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csms.Tests;

/// <summary>
/// Конвейер «обязанность → нормы ответственности» проверяется без реальной
/// модели и БД: детерминированные шаги, фейковый ответ LLM и in-memory store.
/// </summary>
public sealed class ComplianceLiabilityMappingTests
{
    [Fact]
    public void Генератор_строит_четыре_гипотезы_нарушения()
    {
        var duty = CreateDuty();
        var hypotheses = new ViolationHypothesisGenerator().Generate(duty);

        Assert.Equal(4, hypotheses.Count);
        Assert.Equal(
            new[]
            {
                ViolationKind.ProcedureOrFormViolation,
                ViolationKind.DeadlineViolation,
                ViolationKind.SubjectOrAuthorityViolation,
                ViolationKind.Omission,
            },
            hypotheses.Select(item => item.Kind));
        Assert.All(hypotheses, item => Assert.Equal(duty.Identifiers.DutyId, item.DutyId));
        Assert.Contains("порядка", hypotheses[0].Text);
        Assert.Contains("срока", hypotheses[1].Text);
        Assert.Contains("не совершил", hypotheses[3].Text);
    }

    [Fact]
    public void Промпт_содержит_актуальные_пороги_и_калибровку_оценок()
    {
        var systemPrompt = LiabilityArbiterPrompts.SystemPrompt;
        foreach (var threshold in new[]
                 {
                     LiabilityMappingRules.LowProbabilityThreshold,
                     LiabilityMappingRules.PartialReviewThreshold,
                     LiabilityMappingRules.FullReviewThreshold,
                 })
        {
            Assert.Contains(
                threshold.ToString("0.00", CultureInfo.InvariantCulture),
                systemPrompt);
        }

        Assert.Contains("blanket: 0.20–0.60", systemPrompt);

        var userPrompt = LiabilityArbiterPrompts.BuildUserPrompt(
            CreateDuty(), CreateHypotheses(), [Candidate("n1")]);
        Assert.Contains("evaluation_rules", userPrompt);
        Assert.Contains(LiabilityMappingRules.RulesVersion, userPrompt);
    }

    [Fact]
    public void Валидатор_сохраняет_полное_совпадение_и_условия_отдельно()
    {
        var candidates = new[] { Candidate("n1") };
        var response = new LlmArbiterResponse(
        [
            new LlmArbiterItem(
                "n1", MatchDegree.Full, 0.95,
                ["Наступление ущерба"], "Штраф", "Состав прямо совпадает с гипотезой h1", false),
        ]);

        var outcome = new MappingValidator().Validate(response, candidates);

        var match = Assert.Single(outcome.Accepted);
        Assert.Equal(MatchDegree.Full, match.MatchDegree);
        Assert.Equal(0.95, match.ProbabilityScore);
        Assert.Equal(new[] { "Наступление ущерба" }, match.QualifyingConditions);
        Assert.False(match.RequiresHumanReview);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void Валидатор_сохраняет_бланкетную_норму_но_помечает_на_проверку()
    {
        var candidates = new[] { Candidate("n1", LiabilityNormType.Blanket) };
        var response = new LlmArbiterResponse(
        [
            new LlmArbiterItem(
                "n1", MatchDegree.Blanket, 0.9, [], "Общая санкция", "Подпадает под общую норму", false),
        ]);

        var outcome = new MappingValidator().Validate(response, candidates);

        var match = Assert.Single(outcome.Accepted);
        Assert.Equal(MatchDegree.Blanket, match.MatchDegree);
        Assert.True(match.RequiresHumanReview);
    }

    [Fact]
    public void Валидатор_направляет_на_проверку_полное_совпадение_с_низкой_оценкой()
    {
        var candidates = new[] { Candidate("n1") };
        var response = new LlmArbiterResponse([Item("n1", MatchDegree.Full, 0.7)]);

        var outcome = new MappingValidator().Validate(response, candidates);

        Assert.True(Assert.Single(outcome.Accepted).RequiresHumanReview);
    }

    [Fact]
    public void Валидатор_исключает_irrelevant_из_вывода()
    {
        var candidates = new[] { Candidate("n1"), Candidate("n2") };
        var response = new LlmArbiterResponse(
        [
            Item("n1", MatchDegree.Full, 0.9),
            Item("n2", MatchDegree.Irrelevant, 0.99),
        ]);

        var outcome = new MappingValidator().Validate(response, candidates);

        Assert.Single(outcome.Accepted);
        Assert.Equal("n1", outcome.Accepted[0].LiabilityNodeId);
    }

    [Fact]
    public void Валидатор_не_пропускает_норму_вне_пула_кандидатов()
    {
        var candidates = new[] { Candidate("n1") };
        var response = new LlmArbiterResponse([Item("hallucinated", MatchDegree.Full, 0.9)]);

        var outcome = new MappingValidator().Validate(response, candidates);

        Assert.DoesNotContain(
            outcome.Accepted,
            match => match.LiabilityNodeId == "hallucinated");
        Assert.True(outcome.HasHardViolations);
        Assert.Contains(outcome.Violations, text => text.Contains("вне пула кандидатов"));
    }

    [Fact]
    public void Валидатор_направляет_на_проверку_конфликтующие_дубликаты()
    {
        var candidates = new[] { Candidate("n1") };
        var response = new LlmArbiterResponse(
        [
            Item("n1", MatchDegree.Full, 0.9),
            Item("n1", MatchDegree.Partial, 0.4),
        ]);

        var outcome = new MappingValidator().Validate(response, candidates);

        var match = Assert.Single(outcome.Accepted);
        Assert.True(match.RequiresHumanReview);
        Assert.Contains(outcome.Violations, text => text.Contains("конфликтуют"));
    }

    [Fact]
    public void Валидатор_направляет_на_проверку_низкую_вероятность_и_неполные_метаданные()
    {
        var candidates = new[] { Candidate("n1", editionDate: null, status: "") };
        var response = new LlmArbiterResponse([Item("n1", MatchDegree.Partial, 0.3)]);

        var outcome = new MappingValidator().Validate(response, candidates);

        var match = Assert.Single(outcome.Accepted);
        Assert.True(match.RequiresHumanReview);
        Assert.Contains(outcome.Violations, text => text.Contains("неполные метаданные"));
    }

    [Fact]
    public void Валидатор_не_теряет_кандидата_которого_арбитр_не_оценил()
    {
        var candidates = new[] { Candidate("n1"), Candidate("n2") };
        var response = new LlmArbiterResponse([Item("n1", MatchDegree.Full, 0.9)]);

        var outcome = new MappingValidator().Validate(response, candidates);

        Assert.Equal(2, outcome.Accepted.Count);
        var missed = outcome.Accepted.Single(match => match.LiabilityNodeId == "n2");
        Assert.True(missed.RequiresHumanReview);
        Assert.Contains("не вернул оценку", missed.LegalReasoning);
    }

    [Fact]
    public void Валидатор_ограничивает_probability_вне_диапазона()
    {
        var candidates = new[] { Candidate("n1") };
        var response = new LlmArbiterResponse(
        [
            Item("n1", MatchDegree.Full, 1.5) with { ProbabilityScore = 1.5 },
        ]);

        var outcome = new MappingValidator().Validate(response, candidates);

        var match = Assert.Single(outcome.Accepted);
        Assert.Equal(1, match.ProbabilityScore);
        Assert.True(match.RequiresHumanReview);
    }

    [Fact]
    public void Парсер_отвергает_невалидный_JSON()
    {
        Assert.False(LlmArbiterResponseParser.TryParse(
            "{\"results\":[{\"liability_node_id\":\"n1\",\"match_degree\":\"full\"," +
            "\"probability_score\":1.5,\"qualifying_conditions\":[]," +
            "\"sanction_summary\":\"штраф\",\"legal_reasoning\":\"обоснование\"," +
            "\"requires_human_review\":false}]}", out _, out var error));
        Assert.Contains("диапазона", error);

        Assert.False(LlmArbiterResponseParser.TryParse(
            "{\"results\":[],\"unexpected\":1}", out _, out error));
        Assert.Contains("Неизвестное поле", error);

        Assert.False(LlmArbiterResponseParser.TryParse("not json", out _, out error));
        Assert.Contains("корректным JSON", error);
    }

    [Fact]
    public async Task Арбитр_повторяет_невалидный_JSON_и_пишет_аудит()
    {
        var store = new InMemoryLiabilityMappingStore();
        var completion = new FakeCompletionClient(
            "this is not json",
            ValidJson(Item("n1", MatchDegree.Full, 0.9)));
        var arbiter = CreateArbiter(completion, store, maxRetries: 1);

        var response = await arbiter.ArbitrateAsync(
            CreateDuty(), CreateHypotheses(), [Candidate("n1")], Guid.NewGuid());

        Assert.Single(response.Results);
        Assert.Equal("n1", response.Results[0].LiabilityNodeId);
        Assert.Equal(2, completion.Calls);
        Assert.Equal(2, store.Audits.Count);
        Assert.Contains(store.Audits, audit => audit.ValidationResult == "invalid");
        Assert.Contains(store.Audits, audit => audit.ValidationResult == "valid");
    }

    [Fact]
    public async Task Арбитр_после_исчерпания_повторов_передаёт_всех_на_проверку()
    {
        var store = new InMemoryLiabilityMappingStore();
        var completion = new FakeCompletionClient("still invalid");
        var arbiter = CreateArbiter(completion, store, maxRetries: 1);
        var candidates = new[] { Candidate("n1"), Candidate("n2", LiabilityNormType.Blanket) };

        var response = await arbiter.ArbitrateAsync(
            CreateDuty(), CreateHypotheses(), candidates, Guid.NewGuid());

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, item => Assert.True(item.RequiresHumanReview));
        Assert.Equal(3, store.Audits.Count);
        Assert.Contains(store.Audits, audit => audit.ValidationResult == "manual_review");
    }

    [Fact]
    public async Task Сервис_строит_одну_обязанность_ко_многим_нормам()
    {
        var completion = new FakeCompletionClient(ValidJson(
            Item("n1", MatchDegree.Full, 0.95),
            Item("n2", MatchDegree.Partial, 0.8, ["Ущерб"])));
        var service = CreateService(
            new FakeRetriever(Candidate("n1"), Candidate("n2")), completion, out var store);

        var result = await service.MapAsync(CreateDuty());

        Assert.Equal(2, result.MatchedNorms.Count);
        Assert.Equal(2, result.CandidateCount);
        Assert.DoesNotContain(result.MatchedNorms, match => match.MatchDegree == MatchDegree.Irrelevant);
        Assert.Contains(result.MatchedNorms, match => match.ProbabilityScore == 0.95);
        Assert.NotEmpty(store.Audits);
    }

    [Fact]
    public async Task Сервис_пустого_пула_не_вызывает_арбитра()
    {
        var completion = new FakeCompletionClient();
        var service = CreateService(new FakeRetriever(), completion, out _);

        var result = await service.MapAsync(CreateDuty());

        Assert.Empty(result.MatchedNorms);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, completion.Calls);
    }

    [Fact]
    public async Task Сервис_повторного_запуска_не_создаёт_дубли()
    {
        var completion = new FakeCompletionClient(
            ValidJson(Item("n1", MatchDegree.Full, 0.9), Item("n2", MatchDegree.Full, 0.9)));
        var retriever = new FakeRetriever(Candidate("n1"), Candidate("n2"));
        var service = CreateService(retriever, completion, out var store);
        var duty = CreateDuty();

        var first = await service.MapAsync(duty);
        var second = await service.MapAsync(duty);

        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(first.MatchedNorms.Count, second.MatchedNorms.Count);
        Assert.Equal(1, completion.Calls);
        Assert.Equal(2, first.MatchedNorms.Count);
        Assert.Equal(2, second.MatchedNorms.Count);
        Assert.NotNull(await store.GetResultAsync(first.RunId, duty.Identifiers.DutyId));
    }

    [Fact]
    public void Батчинг_не_теряет_длинные_тексты_и_усекает_их_в_промпте()
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(index => Candidate($"n{index}", text: new string('а', 5_000)))
            .ToArray();

        var batches = LiabilityBatching.Split(candidates);

        Assert.Equal(2, batches.Count);
        Assert.Equal(10, batches.Sum(batch => batch.Count));
        var prompt = LiabilityArbiterPrompts.BuildUserPrompt(
            CreateDuty(), CreateHypotheses(), batches[0]);
        Assert.Contains("текст усечён", prompt);
    }

    [Fact]
    public void Ключ_запуска_детерминирован_и_зависит_от_пула()
    {
        var duty = CreateDuty();
        var candidates = new[] { Candidate("n1"), Candidate("n2") };

        var first = LiabilityMappingRules.BuildRunId(duty, candidates, "model-v1");
        var second = LiabilityMappingRules.BuildRunId(
            duty, candidates.Reverse().ToArray(), "model-v1");
        var otherModel = LiabilityMappingRules.BuildRunId(duty, candidates, "model-v2");
        var otherPool = LiabilityMappingRules.BuildRunId(
            duty, [Candidate("n1")], "model-v1");
        var otherConfig = LiabilityMappingRules.BuildRunId(
            duty, candidates, "model-v1", "config-v2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherModel);
        Assert.NotEqual(first, otherPool);
        Assert.NotEqual(first, otherConfig);
        Assert.Contains(LiabilityMappingRules.RulesVersion, LiabilityMappingRules.BuildRulesFingerprint());
    }

    [Fact]
    public void Отпечаток_настроек_реагирует_на_все_параметры_модели()
    {
        var options = new LiabilityMappingOptions { Model = "fake-model" };
        var previous = options.BuildConfigFingerprint();

        options.Temperature = 0.1;
        Assert.NotEqual(previous, options.BuildConfigFingerprint());
        previous = options.BuildConfigFingerprint();

        options.MaxOutputTokens = 8192;
        Assert.NotEqual(previous, options.BuildConfigFingerprint());
        previous = options.BuildConfigFingerprint();

        options.ContextTokens = 16384;
        Assert.NotEqual(previous, options.BuildConfigFingerprint());
        previous = options.BuildConfigFingerprint();

        options.ConfigVersion = "liability-config-v4";
        Assert.NotEqual(previous, options.BuildConfigFingerprint());
    }

    [Fact]
    public void Результат_сериализуется_в_оговорённый_контракт_API()
    {
        var result = new LiabilityMappingResult(
            "duty-1",
            Guid.NewGuid(),
            CreateHypotheses(),
            [
                new MatchedLiability(
                    "n1", "ст. 15", MatchDegree.Full, 0.9,
                    [], "штраф", "Обоснование", false),
            ],
            1, "model-v1", LiabilityMappingRules.PromptVersion,
            DateTimeOffset.UtcNow, false);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("duty_id", out _));
        Assert.True(root.TryGetProperty("violation_hypotheses", out var hypotheses));
        Assert.NotEmpty(hypotheses.EnumerateArray());
        Assert.True(root.TryGetProperty("matched_liabilities", out var matched));
        var item = Assert.Single(matched.EnumerateArray());
        foreach (var name in new[]
                 {
                     "liability_node_id", "norm_reference", "match_degree",
                     "probability_score", "qualifying_conditions", "sanction_summary",
                     "legal_reasoning", "requires_human_review",
                 })
        {
            Assert.True(item.TryGetProperty(name, out _), name);
        }

        Assert.Equal("full", item.GetProperty("match_degree").GetString());
    }

    private static DutyInput CreateDuty() => new(
        "Подготовить отчёт",
        "отчёт",
        "Начальник отдела",
        "до 31.12.2026",
        "при наличии оснований",
        new DutyIdentifiers("duty-1", "doc-1", "15", "1", "3", "ext-1"),
        "44-FZ");

    private static IReadOnlyList<ViolationHypothesis> CreateHypotheses() =>
        new ViolationHypothesisGenerator().Generate(CreateDuty());

    private static LiabilityCandidate Candidate(
        string id,
        LiabilityNormType normType = LiabilityNormType.Special,
        DateOnly? editionDate = null,
        string? status = "in_force",
        string? text = null) =>
        new(
            id,
            $"ст. 15 ч. 1 п. {id}",
            text ?? "За нарушение требований надлежащему исполнению обязанности применяется штраф.",
            "44-FZ",
            "v1",
            normType,
            status ?? string.Empty,
            editionDate ?? new DateOnly(2024, 1, 1),
            new Dictionary<string, string> { ["ked"] = "15.1" },
            CandidateSource.Merged,
            0.9);

    private static LlmArbiterItem Item(
        string id, MatchDegree degree, double score, IReadOnlyList<string>? conditions = null) =>
        new(id, degree, score, conditions ?? [], "штраф", "Обоснование арбитра", false);

    private static string ValidJson(params LlmArbiterItem[] items) =>
        JsonSerializer.Serialize(new
        {
            results = items.Select(item => new
            {
                liability_node_id = item.LiabilityNodeId,
                match_degree = item.MatchDegree.ToString().ToLowerInvariant(),
                probability_score = item.ProbabilityScore,
                qualifying_conditions = item.QualifyingConditions,
                sanction_summary = item.SanctionSummary,
                legal_reasoning = item.LegalReasoning,
                requires_human_review = item.RequiresHumanReview,
            }).ToArray(),
        });

    private static LlmArbiter CreateArbiter(
        ILlmCompletionClient completion,
        ILiabilityMappingStore store,
        int maxRetries)
    {
        var options = new LiabilityMappingOptions
        {
            Model = "fake-model",
            MaxRetries = maxRetries,
            RetryBaseDelayMs = 0,
        };
        return new LlmArbiter(
            completion, store, options, NullLogger<LlmArbiter>.Instance);
    }

    private static DutyLiabilityMappingService CreateService(
        ILiabilityRetriever retriever,
        ILlmCompletionClient completion,
        out InMemoryLiabilityMappingStore store)
    {
        store = new InMemoryLiabilityMappingStore();
        var options = new LiabilityMappingOptions { Model = "fake-model", MaxRetries = 0 };
        return new DutyLiabilityMappingService(
            new ViolationHypothesisGenerator(),
            retriever,
            CreateArbiter(completion, store, options.MaxRetries),
            new MappingValidator(),
            store,
            options,
            NullLogger<DutyLiabilityMappingService>.Instance);
    }

    private sealed class FakeCompletionClient(params string[] responses) : ILlmCompletionClient
    {
        private readonly Queue<string> _responses = new(responses);

        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : responses[^1]);
        }
    }

    private sealed class FakeRetriever(params LiabilityCandidate[] candidates) : ILiabilityRetriever
    {
        public Task<IReadOnlyList<LiabilityCandidate>> RetrieveAsync(
            DutyInput duty,
            IReadOnlyList<ViolationHypothesis> hypotheses,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiabilityCandidate>>(candidates);
    }
}
