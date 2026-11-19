using Csms.Application;
using Csms.Infrastructure;
using Csms.Infrastructure.Npa;

namespace Csms.Tests;

/// <summary>
/// Проверки агента разметки НПА без Tesseract, портала и PostgreSQL:
/// нормализация кодов, классификация оговорок, дерево ОКПД2 и версии правил.
/// </summary>
public sealed class NpaTests
{
    [Fact]
    public void Код_ОКПД2_нормализуется_и_не_смешивает_родственные_ветки()
    {
        Assert.Equal("26.20.11.110", NpaRuleSets.NormalizeCode(" 26,20.11.110. "));
        Assert.Equal("26.20.11", NpaRuleSets.ParentOf("26.20.11.110"));
        Assert.Null(NpaRuleSets.ParentOf("26"));

        Assert.True(NpaRuleSets.IsUnder("26.20.11.110", "26.20"));
        // 01.4 и 01.41 — разные уровни, а не предок и потомок.
        Assert.False(NpaRuleSets.IsUnder("01.41", "01.4"));
        Assert.True(NpaRuleSets.IsUnder("01.4.1", "01.4"));
    }

    [Fact]
    public void Справочник_отвергает_нецифровые_коды_других_классификаторов()
    {
        var catalog = Okpd2Catalog.FromItems([
            new ReferenceItemInfo("B", null, "Прочее", true, "{}"),
            new ReferenceItemInfo("26.20", null, "Компьютеры", true, "{}"),
        ]);

        Assert.False(catalog.Contains("B"));
        Assert.True(catalog.Contains("26.20"));
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Резолвер_отвергает_шум_и_восстанавливает_код_без_точек()
    {
        var catalog = Catalog(
            ("26.20.11.110", "Компьютеры"),
            ("26.60.11.110", "Радиоаппаратура"));
        var resolver = new Okpd2CodeResolver(catalog);

        Assert.Equal("catalog", resolver.Resolve("26.20.11.110")!.Method);
        Assert.Equal("26.20.11.110", resolver.Resolve("262011110")!.Code);
        Assert.Equal("digits", resolver.Resolve("262011110")!.Method);

        // Номер пункта и сумма в справочнике отсутствуют — в правила не попадут.
        Assert.Null(resolver.Resolve("12.34.56.789"));
        Assert.Null(resolver.Resolve("1 500 000"));
        Assert.Null(resolver.Resolve("п. 5"));
    }

    [Fact]
    public void Классификатор_различает_включение_оговорку_и_условие()
    {
        var include = NpaRuleClassifier.Decide("Компьютеры", null, 1.0, 0.8);
        Assert.Equal(NpaRuleTypes.Include, include.RuleType);
        Assert.Equal(NpaRuleStatuses.Auto, include.Status);

        var exclude = NpaRuleClassifier.Decide(null, "кроме кода 26.60", 0.9, 0.8);
        Assert.Equal(NpaRuleTypes.Exclude, exclude.RuleType);
        Assert.Contains("кроме", exclude.Note, StringComparison.Ordinal);

        // Уточнение внутри наименования не исключает всю строку перечня.
        var scoped = NpaRuleClassifier.Decide("Древесина и изделия из пробки (кроме мебели)", null, 1.0, 0.8);
        Assert.Equal(NpaRuleTypes.Include, scoped.RuleType);

        var conditional = NpaRuleClassifier.Decide("ПО", "при условии отечественного происхождения", 1.0, 0.8);
        Assert.Equal(NpaRuleTypes.Conditional, conditional.RuleType);
        Assert.Equal(NpaRuleStatuses.NeedsReview, conditional.Status);

        var noName = NpaRuleClassifier.Decide(null, null, 0.45, 0.8);
        Assert.Equal(NpaRuleStatuses.NeedsReview, noName.Status);
    }

    [Fact]
    public void Билдер_выносит_коды_из_оговорки_строки_в_отдельные_исключения()
    {
        var catalog = Catalog(
            ("23", "Продукты минеральные неметаллические прочие"),
            ("23.13.13.140", "Цемент"),
            ("23.13.13.142", "Цемент прочий"));
        var parsed = new NpaParsedDocument(
            [
                new NpaTableRow(
                    1,
                    1,
                    "23",
                    "Продукты минеральные неметаллические прочие",
                    "Продукты минеральные неметаллические прочие (кроме кодов 23.13.13.140, 23.13.13.142)",
                    1.0,
                    "test"),
            ],
            [],
            [],
            1);

        var rules = NpaRuleBuilder.Build(parsed, 0.8, catalog);

        Assert.Equal(NpaRuleTypes.Include, rules.Single(rule => rule.Okpd2Code == "23").RuleType);
        Assert.Equal(NpaRuleTypes.Exclude, rules.Single(rule => rule.Okpd2Code == "23.13.13.140").RuleType);
        Assert.Equal(NpaRuleTypes.Exclude, rules.Single(rule => rule.Okpd2Code == "23.13.13.142").RuleType);
    }

    [Fact]
    public void Билдер_понимает_перенос_оговорки_на_следующую_строку()
    {
        var catalog = Catalog(
            ("25", "Изделия металлические готовые"),
            ("25.30.2", "Сосуды"),
            ("25.71.14.120", "Замки"),
            ("25.40.1", "Изделия из алюминия"),
            ("26", "Оборудование компьютерное"));
        var parsed = new NpaParsedDocument(
            [
                new NpaTableRow(
                    1, 1, "25", "Изделия металлические готовые",
                    "Изделия металлические готовые (кроме кодов 25.30.2,", 1.0, "test"),
                new NpaTableRow(
                    1, 2, "26", "25.71.14.120, 25.40.1) Оборудование компьютерное",
                    "25.71.14.120, 25.40.1) Оборудование компьютерное", 1.0, "test"),
            ],
            [],
            [],
            1);

        var rules = NpaRuleBuilder.Build(parsed, 0.8, catalog);

        Assert.Equal(NpaRuleTypes.Include, rules.Single(rule => rule.Okpd2Code == "25").RuleType);
        Assert.Equal(NpaRuleTypes.Include, rules.Single(rule => rule.Okpd2Code == "26").RuleType);
        foreach (var code in new[] { "25.30.2", "25.71.14.120", "25.40.1" })
        {
            Assert.Equal(NpaRuleTypes.Exclude, rules.Single(rule => rule.Okpd2Code == code).RuleType);
        }
    }

    [Fact]
    public void Билдер_достаёт_коды_из_обрывков_оговорки_в_неразобранных_строках()
    {
        var catalog = Catalog(
            ("01.4", "Продукция животноводства"),
            ("01.7", "Охота"),
            ("26.60.12.129", "Аппараты"),
            ("26.60.11.114", "Аппараты"),
            ("26.60.11.119", "Аппараты"),
            ("31", "Мебель"));
        var parsed = new NpaParsedDocument(
            [],
            [],
            [
                new NpaUnresolvedRow(2, 2, "01.4, 01.7) охоты (кроме кодов", "нет кода ОКПД2 в колонке"),
                new NpaUnresolvedRow(3, 21, "26.60.12.129, 26.60.11.114, 26.60.11.119,", "нет кода ОКПД2 в колонке"),
                new NpaUnresolvedRow(1, 4, "Российской 31 октября Федерации от", "нет кода ОКПД2 в колонке"),
            ],
            1);

        var rules = NpaRuleBuilder.Build(parsed, 0.8, catalog);

        foreach (var code in new[] { "01.4", "01.7", "26.60.12.129", "26.60.11.114", "26.60.11.119" })
        {
            Assert.Equal(NpaRuleTypes.Exclude, rules.Single(rule => rule.Okpd2Code == code).RuleType);
        }

        // «31 октября» — дата, а не код мебели 31.
        Assert.DoesNotContain(rules, rule => rule.Okpd2Code == "31");
    }

    [Fact]
    public void Код_в_неразобранной_строке_не_становится_исключением_из_за_кроме_в_наименовании()
    {
        var catalog = Catalog(
            ("30", "Средства транспортные прочие"),
            ("30.11.23", "Суда рефрижераторные"),
            ("25.71.11", "Ножи"),
            ("26", "Оборудование компьютерное"));
        var parsed = new NpaParsedDocument(
            [],
            [],
            [
                new NpaUnresolvedRow(39, 2, "121. 30.11.23 Суда рефрижераторные, кроме танкеров", "нет кода ОКПД2 в колонке"),
                new NpaUnresolvedRow(49, 3, "102. 25.71.11 Ножи — (кроме машин) и ножницы", "нет кода ОКПД2 в колонке"),
            ],
            1);

        var rules = NpaRuleBuilder.Build(parsed, 0.8, catalog);

        // «кроме танкеров» и «кроме машин» уточняют наименование позиции.
        foreach (var rule in rules)
        {
            Assert.Equal(NpaRuleTypes.Include, rule.RuleType);
            Assert.Equal(NpaRuleStatuses.NeedsReview, rule.Status);
        }

        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, rule => rule.Okpd2Code == "26");
    }

    [Fact]
    public void Дерево_протягивает_правила_но_исключение_сильнее_предка()
    {
        var catalog = Catalog(
            ("26", "ЭВМ"),
            ("26.20", "Компьютеры"),
            ("26.20.11.110", "Компьютеры настольные"),
            ("26.60", "Радиоаппаратура"),
            ("26.60.11.110", "Радиостанции"));

        var rules = NpaRuleHierarchy.Propagate(
        [
            Rule("26", NpaRuleTypes.Include),
            Rule("26.60", NpaRuleTypes.Exclude),
        ], catalog);

        var child = rules.Single(rule => rule.Okpd2Code == "26.20.11.110");
        Assert.True(child.IsInherited);
        Assert.Equal("26", child.InheritedFrom);
        Assert.Equal(NpaRuleTypes.Include, child.RuleType);

        var excluded = rules.Single(rule => rule.Okpd2Code == "26.60.11.110");
        Assert.Equal(NpaRuleTypes.Exclude, excluded.RuleType);
        Assert.Equal("26.60", excluded.InheritedFrom);

        Assert.DoesNotContain(rules, rule => rule.Okpd2Code == "26" && rule.IsInherited);
    }

    [Fact]
    public void Действующее_правило_берёт_самое_длинное_совпадение()
    {
        var rules = new List<NpaRuleInfo>
        {
            Info("26", NpaRuleTypes.Include),
            Info("26.60", NpaRuleTypes.Exclude),
            Info("26.60.11", NpaRuleTypes.Conditional, isInherited: true),
        };

        var match = NpaRuleHierarchy.Resolve(rules, "26.60.11.110");
        Assert.Equal("26.60.11", match!.Okpd2Code);
        Assert.Equal(NpaRuleTypes.Conditional, match.RuleType);

        Assert.Equal("26", NpaRuleHierarchy.Resolve(rules, "26.20.11.110")!.Okpd2Code);
        Assert.Null(NpaRuleHierarchy.Resolve(rules, "32.50.13.190"));
    }

    [Fact]
    public async Task Хранилище_не_перезаписывает_версии_и_фильтрует_на_проверку()
    {
        var store = new InMemoryNpaRuleStore();
        var first = Import("v1", Rule("26.20", NpaRuleTypes.Include));
        var second = Import("v2",
            Rule("26.20", NpaRuleTypes.Include),
            Rule("26.60", NpaRuleTypes.Exclude, NpaRuleStatuses.NeedsReview));

        Assert.Equal("unknown", (await store.PublishAsync(first)).FromVersion);
        await store.PublishAsync(second);

        var documents = await store.GetDocumentsAsync();
        Assert.Equal(2, documents.Count);
        Assert.Single(documents, document => document.IsActive);
        Assert.Equal("v2", (await store.GetActiveAsync("rp-471"))!.Version);
        Assert.True(await store.VersionExistsAsync("rp-471", "v2"));
        Assert.False(await store.VersionExistsAsync("rp-471", "v3"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PublishAsync(second));

        var pending = await store.GetRulesAsync("rp-471", null, NpaRuleStatuses.NeedsReview, 0, 100);
        Assert.Single(pending);
        Assert.Equal("26.60", pending[0].Okpd2Code);

        // Спорное правило не применяется без подтверждения человеком.
        Assert.Null(await store.ResolveAsync("rp-471", "26.60.11.110", includePending: false));
        Assert.NotNull(await store.ResolveAsync("rp-471", "26.60.11.110", includePending: true));

        var verified = await store.VerifyAsync(
            "rp-471", "26.60", NpaRuleStatuses.Approved, "юрист подтвердил");
        Assert.Equal(NpaRuleStatuses.Approved, verified!.Status);
        Assert.NotNull(await store.ResolveAsync("rp-471", "26.60.11.110", includePending: false));
    }

    [Fact]
    public void Расписание_проверки_задаётся_в_московском_времени()
    {
        var options = new NpaOptions();
        Assert.Equal("03:00", options.CheckTime);
        Assert.Equal(30, options.CheckDelayMinutes);
        Assert.True(options.TryGetCheckTime(out var time));
        Assert.Equal(new TimeOnly(3, 0), time);

        options.CheckTime = "25:00";
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public async Task Хранилище_отделяет_время_проверки_от_времени_публикации_версии()
    {
        var store = new InMemoryNpaRuleStore();
        await store.PublishAsync(Import("v1", Rule("26.20", NpaRuleTypes.Include)));

        var checkedAt = new DateTimeOffset(2026, 11, 20, 3, 15, 0, TimeSpan.FromHours(3));
        var marked = await store.MarkCheckedAsync(
            "rp-471",
            NpaCheckStatuses.UpToDate,
            "Официальный текст не изменился.",
            checkedAt);

        Assert.NotNull(marked);
        Assert.Equal(checkedAt, marked!.LastCheckedAt);
        Assert.Equal(NpaCheckStatuses.UpToDate, marked.LastCheckStatus);

        var active = await store.GetActiveAsync("rp-471");
        Assert.Equal(checkedAt, active!.LastCheckedAt);
        Assert.NotEqual(checkedAt, active.FetchedAt);
        Assert.Equal(NpaCheckStatuses.UpToDate, active.LastCheckStatus);
        Assert.Equal("Официальный текст не изменился.", active.LastCheckMessage);
    }

    [Fact]
    public void Парсер_разложки_отделяет_код_строки_от_оговорки_кроме()
    {
        var catalog = Catalog(("26.20", "Компьютеры"), ("26.60", "Радиоаппаратура"));
        var document = new NpaDocumentOptions
        {
            Code = "rp-471",
            TableLayout = true,
            CodeColumnStart = 0.02,
        };
        var page = new OcrPage(
            1,
            2000,
            1000,
            [
                Word(400, 100, "Компьютеры"),
                Word(400, 200, "кроме"),
                Word(600, 200, "радиоаппаратуры"),
            ],
            [
                Word(200, 100, "26.20", 90),
                Word(900, 200, "26.60", 90),
            ]);

        var result = new NpaLayoutParser().Parse(page, document, new Okpd2CodeResolver(catalog));

        Assert.Equal("26.20", Assert.Single(result.Rows).Code);
        Assert.Equal("Компьютеры", result.Rows[0].Name);
        Assert.Equal("26.60", Assert.Single(result.Exclusions).Code);
    }

    private static Okpd2Catalog Catalog(params (string Code, string Name)[] items) =>
        Okpd2Catalog.FromItems(items.Select(item => new ReferenceItemInfo(
            item.Code, null, item.Name, true, "{}")));

    private static NpaRuleImportItem Rule(
        string code,
        string type,
        string status = NpaRuleStatuses.Auto) =>
        new(code, type, null, "цитата", false, null, 0.95, status, null, 1, 1);

    private static NpaRuleInfo Info(
        string code,
        string type,
        bool isInherited = false,
        string status = NpaRuleStatuses.Auto) =>
        new("rp-471", code, type, null, null, isInherited, null, 0.95, status, null, 1, 1);

    private static NpaDocumentImport Import(string version, params NpaRuleImportItem[] rules) =>
        new(
            "rp-471", version, "0001201603230014", "471-р", "471-р",
            null, null, 7, null, "sha", "test", rules);

    private static OcrWord Word(int left, int top, string text, double confidence = 95) =>
        new(left, top, text.Length * 20, 30, text, confidence);
}
