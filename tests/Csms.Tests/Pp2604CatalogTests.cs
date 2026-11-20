using Csms.Application;

namespace Csms.Tests;

/// <summary>
/// Подбор позиции приложения N 2 по БД-каталогу 2604. Тест фиксирует,
/// что правила читаются из переданной версии, а не из констант фронтенда:
/// приоритет цели закупки, longest prefix и обязательный источник связи.
/// </summary>
public class Pp2604CatalogTests
{
    private static Pp2604Mapping Mapping() =>
        new(
            "pp-2604-test",
            "pp2604-rules-v1",
            "0001202201040005",
            "http://publication.pravo.gov.ru/api/Documents",
            "http://publication.pravo.gov.ru/file/pdf?eoNumber=0001202201040005",
            "sha256",
            "ПП РФ N 2604",
            "2021-12-31",
            "2022-01-04",
            32,
            "27–29",
            "тестовый каталог",
            DateTimeOffset.UnixEpoch,
            new Pp2604SourceInfo(
                "Постановление Правительства РФ от 31.12.2021 N 2604",
                "Приложение N 2",
                "publication.pravo.gov.ru",
                "0001202201040005",
                "27–29",
                "ОКПД2 НСИ ЕИС"),
            [
                new Pp2604CategoryLimit(
                    "works_services", "Работы, услуги", 2, "Работы, услуги", 60, 10, 40, 27),
                new Pp2604CategoryLimit(
                    "construction", "Строительство", 3, "Строительство", 60, null, 40, 27),
                new Pp2604CategoryLimit(
                    "road", "Автодороги", 4, "Автодороги", 60, null, 40, 27),
                new Pp2604CategoryLimit(
                    "food_catering", "Питание", 13, "Питание", 40, null, 60, 27),
            ],
            [
                new Pp2604Okpd2Link(
                    "", "works_services", 2, "residual", "Остаток", null, false,
                    null, "остаток позиции 2", 100),
                new Pp2604Okpd2Link(
                    "42", "construction", 3, "classifier", "Сооружения", null, false,
                    null, "поз. 3 / раздел 42", 10),
                new Pp2604Okpd2Link(
                    "42.11", "road", 4, "classifier", "Автомобильные дороги",
                    "подтвердите вид работ", true, null, "поз. 4 / код 42.11", 11),
                new Pp2604Okpd2Link(
                    "10", "food_catering", 13, "purpose", "Продукты пищевые",
                    "для социальных организаций", false, "food_for_social_orgs",
                    "поз. 13 / цель закупки", -1),
            ]);

    [Fact]
    public void Resolve_LongestPrefixWins_AndKeepsReviewFlag()
    {
        var result = Pp2604CategoryResolver.Resolve(Mapping(), "42.11.21");

        Assert.Equal("road", result.Category);
        Assert.Equal(4, result.Row);
        Assert.Equal("classifier", result.Basis);
        Assert.True(result.NeedsReview);
        Assert.Contains("поз. 4", result.Source ?? string.Empty);
    }

    [Fact]
    public void Resolve_CommonPrefixIsUsed_WhenNoLongerMatch()
    {
        var result = Pp2604CategoryResolver.Resolve(Mapping(), "42.12");

        Assert.Equal("construction", result.Category);
        Assert.Equal(3, result.Row);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public void Resolve_PurposeRuleOverridesClassifierPrefix()
    {
        var result = Pp2604CategoryResolver.Resolve(
            Mapping(), "10.11.11", "food_for_social_orgs");

        Assert.Equal("food_catering", result.Category);
        Assert.Equal(13, result.Row);
        Assert.Equal("purpose", result.Basis);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public void Resolve_ResidualRuleCoversUnknownPrefix()
    {
        var result = Pp2604CategoryResolver.Resolve(Mapping(), "99.1");

        Assert.Equal("works_services", result.Category);
        Assert.Equal("residual", result.Basis);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10..11")]
    public void Resolve_InvalidCode_RequiresReview(string? code)
    {
        var result = Pp2604CategoryResolver.Resolve(Mapping(), code);

        Assert.Null(result.Category);
        Assert.True(result.NeedsReview);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }
}
