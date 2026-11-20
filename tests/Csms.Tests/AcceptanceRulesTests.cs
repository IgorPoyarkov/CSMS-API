using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Правила ст. 94 44-ФЗ: что считается корректной приёмкой, какие условия
/// контракта обязательны и когда итог приёмки противоречит сам себе.
/// </summary>
public class AcceptanceRulesTests
{
    private static IReadOnlyList<AcceptanceIssue> Validate(
        AcceptanceInput input,
        AcceptanceStatus status = AcceptanceStatus.Draft,
        IReadOnlyList<AcceptancePosition>? positions = null,
        IReadOnlyList<AcceptanceDefect>? defects = null) =>
        AcceptanceRules.Validate(input, positions ?? [], defects ?? [], status);

    private static void AssertHasError(
        IReadOnlyList<AcceptanceIssue> issues,
        string field,
        string? contains = null)
    {
        var matches = issues.Where(issue => issue.IsError && issue.Field == field).ToList();

        Assert.True(
            matches.Count > 0,
            $"Ожидалась ошибка по полю «{field}», получено: {Describe(issues)}");

        if (contains is not null)
        {
            Assert.True(
                matches.Any(issue => issue.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)),
                $"В ошибках по полю «{field}» нет текста «{contains}»: {Describe(matches)}");
        }
    }

    private static void AssertNoErrors(IReadOnlyList<AcceptanceIssue> issues) =>
        Assert.True(
            issues.All(issue => !issue.IsError),
            $"Ошибок быть не должно: {Describe(issues)}");

    private static string Describe(IEnumerable<AcceptanceIssue> issues) =>
        string.Join(" | ", issues.Select(issue => $"{issue.Severity}/{issue.Field}: {issue.Message}"));

    // ─── Корректные документы ───────────────────────────────────────────────

    [Fact]
    public void Draft_WithoutPositions_HasNoErrors()
    {
        // Черновик сохраняется до формирования перечня: иначе начатый документ
        // нельзя было бы даже записать.
        AssertNoErrors(Validate(AcceptanceSamples.Primary()));
    }

    [Fact]
    public void Submitted_WithPosition_HasNoErrors()
    {
        AssertNoErrors(Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.Submitted,
            [AcceptanceSamples.Position()]));
    }

    [Fact]
    public void Submitted_WithoutPositions_Reported()
    {
        var issues = Validate(AcceptanceSamples.Primary(), AcceptanceStatus.Submitted);

        AssertHasError(issues, "positions", "Не указана ни одна позиция");
    }

    // ─── Снимок контракта ───────────────────────────────────────────────────

    [Fact]
    public void MissingContractFields_Reported()
    {
        var input = AcceptanceSamples.Primary() with
        {
            OrganizationId = " ",
            ContractId = string.Empty,
            ContractNumber = "",
            ContractName = "",
            ContractPrice = 0,
            ContractDate = null,
        };

        var issues = Validate(input);

        AssertHasError(issues, "organizationId");
        AssertHasError(issues, "contractId");
        AssertHasError(issues, "contractNumber");
        AssertHasError(issues, "contractName");
        AssertHasError(issues, "contractPrice");
        AssertHasError(issues, "contractDate");
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("terminated")]
    public void ClosedContract_CannotBeAccepted(string status)
    {
        var issues = Validate(AcceptanceSamples.Primary(contractStatus: status));

        AssertHasError(issues, "contractStatus", "принять по нему больше нечего");
    }

    [Fact]
    public void ContractInDraftStatus_CannotBeAccepted()
    {
        var issues = Validate(AcceptanceSamples.Primary(contractStatus: "draft"));

        AssertHasError(issues, "contractStatus", "только по заключённому или исполняемому");
    }

    [Fact]
    public void DocumentDateBeforeContractDate_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(documentDate: new DateOnly(2026, 8, 1)));

        AssertHasError(issues, "documentDate", "раньше даты заключения контракта");
    }

    [Fact]
    public void Submitted_WithoutDocumentNumber_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(documentNumber: null),
            AcceptanceStatus.Submitted,
            [AcceptanceSamples.Position()]);

        AssertHasError(issues, "documentNumber", "Не указан номер документа");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void ReviewTermOutOfRange_Reported(int term)
    {
        var issues = Validate(AcceptanceSamples.Primary(acceptanceTermDays: term));

        AssertHasError(issues, "acceptanceTermDays", "от 1 до 20 рабочих дней");
    }

    [Fact]
    public void StageNumberMustBePositive()
    {
        var issues = Validate(AcceptanceSamples.Primary(stageNumber: 0));

        AssertHasError(issues, "stageNumber", "больше нуля");
    }

    // ─── Вид документа ──────────────────────────────────────────────────────

    [Fact]
    public void Correction_RequiresReferenceAndReason()
    {
        var issues = Validate(AcceptanceSamples.Primary(kind: AcceptanceKind.Correction));

        AssertHasError(issues, "correctedDocument", "исправляемого документа");
        AssertHasError(issues, "correctionReason", "причину исправления");
    }

    [Fact]
    public void Correction_DateBeforeCorrectedDocument_Reported()
    {
        var issues = Validate(AcceptanceSamples.Correction(
            correctedDocumentDate: new DateOnly(2026, 11, 15)));

        AssertHasError(issues, "documentDate", "раньше исправляемого");
    }

    [Fact]
    public void Correction_WithReferenceAndReason_HasNoErrors()
    {
        AssertNoErrors(Validate(AcceptanceSamples.Correction()));
    }

    [Fact]
    public void Primary_WithCorrectionFields_Reported()
    {
        var issues = Validate(AcceptanceSamples.Primary(
            correctedDocumentNumber: "АКТ-1",
            correctedDocumentDate: AcceptanceSamples.DocumentDate));

        AssertHasError(issues, "correctedDocument", "Для первичного документа");
    }

    // ─── Экспертиза и комиссия ──────────────────────────────────────────────

    [Fact]
    public void ThirdPartyExpertise_WithoutContractRequirement_Reported()
    {
        var issues = Validate(AcceptanceSamples.Primary(
            expertiseRequired: false,
            expertiseByThirdParty: true));

        AssertHasError(issues, "expertise", "не предусмотрена условиями контракта");
    }

    [Fact]
    public void ThirdPartyExpertise_RequiresOrganizationAndConclusion()
    {
        var issues = Validate(AcceptanceSamples.Primary(expertiseByThirdParty: true));

        AssertHasError(issues, "expertiseOrganization", "организацию");
        AssertHasError(issues, "expertiseConclusion", "заключения экспертизы");
    }

    [Fact]
    public void ExpertiseConclusionAfterDocument_Reported()
    {
        var input = AcceptanceSamples.Primary(expertiseByThirdParty: true) with
        {
            ExpertiseOrganization = "ООО «Эксперт»",
            ExpertiseConclusionNumber = "Э-12",
            ExpertiseConclusionDate = AcceptanceSamples.DocumentDate.AddDays(1),
        };

        var issues = Validate(input);

        AssertHasError(issues, "expertiseConclusion", "не может быть позже даты документа");
    }

    [Fact]
    public void ExpertiseByThirdParty_WithRequisites_HasNoErrors()
    {
        var input = AcceptanceSamples.Primary(expertiseByThirdParty: true) with
        {
            ExpertiseOrganization = "ООО «Эксперт»",
            ExpertiseConclusionNumber = "Э-12",
            ExpertiseConclusionDate = AcceptanceSamples.DocumentDate.AddDays(-1),
        };

        AssertNoErrors(Validate(input));
    }

    [Fact]
    public void Commission_RequiredButMissing_Reported()
    {
        var issues = Validate(AcceptanceSamples.Primary(commissionRequired: true));

        AssertHasError(issues, "commission", "решения о создании приёмочной комиссии");
    }

    [Fact]
    public void CommissionDecisionAfterDocument_Reported()
    {
        var input = AcceptanceSamples.Primary(commissionRequired: true) with
        {
            CommissionDecisionNumber = "12-р",
            CommissionDecisionDate = AcceptanceSamples.DocumentDate.AddDays(2),
        };

        var issues = Validate(input);

        AssertHasError(issues, "commission", "не может быть позже даты документа");
    }

    [Fact]
    public void Commission_NotRequired_IsWarningOnly()
    {
        var input = AcceptanceSamples.Primary() with
        {
            CommissionDecisionNumber = "12-р",
            CommissionDecisionDate = AcceptanceSamples.DocumentDate.AddDays(-2),
        };

        var issues = Validate(input);

        AssertNoErrors(issues);
        Assert.Contains(issues, issue =>
            !issue.IsError && issue.Field == "commission" && issue.Message.Contains("не предусмотрена"));
    }

    // ─── Позиции ────────────────────────────────────────────────────────────

    [Fact]
    public void Positions_TotalAboveContractPrice_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(contractPrice: 10_000m),
            AcceptanceStatus.Submitted,
            [AcceptanceSamples.Position()]);

        AssertHasError(issues, "positions", "превышает цену контракта");
    }

    [Fact]
    public void Correction_TotalAboveContractPrice_Allowed()
    {
        var issues = Validate(
            AcceptanceSamples.Correction() with { ContractPrice = 10_000m },
            AcceptanceStatus.Submitted,
            [AcceptanceSamples.Position()]);

        AssertNoErrors(issues);
    }

    [Fact]
    public void Position_WithUnsupportedVatRate_Reported()
    {
        var position = AcceptancePosition.Restore(
            Guid.NewGuid(), "Шприц 5 мл", "32.50.13.190", "шт.", 100m, 200m, 13m, null, null);

        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.Submitted,
            [position]);

        AssertHasError(issues, "positions", "не применяется");
    }

    [Fact]
    public void PositionTotals_AreConsistent()
    {
        var position = AcceptanceSamples.Position(quantity: 3m, unitPrice: 1000m, vatRate: 20m);

        Assert.Equal(3000m, position.TotalWithVat);
        Assert.Equal(500m, position.VatAmount);
        Assert.Equal(2500m, position.TotalWithoutVat);
    }

    // ─── Недостатки и итог приёмки ──────────────────────────────────────────

    [Fact]
    public void AcceptedWithDefects_Reported()
    {
        var position = AcceptanceSamples.Position();

        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.Accepted,
            [position],
            [AcceptanceSamples.Defect(position.Id)]);

        AssertHasError(issues, "defects", "не может содержать претензии");
    }

    [Fact]
    public void AcceptedWithDiscrepancies_WithoutDefects_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.AcceptedWithDiscrepancies,
            [AcceptanceSamples.Position()]);

        AssertHasError(issues, "defects", "требует перечня недостатков");
    }

    [Fact]
    public void Refused_WithoutDefects_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.Refused,
            [AcceptanceSamples.Position()]);

        AssertHasError(issues, "defects", "должен содержать перечень недостатков");
    }

    [Fact]
    public void Defect_QuantityAbovePosition_Reported()
    {
        var position = AcceptanceSamples.Position(quantity: 10m);

        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.AcceptedWithDiscrepancies,
            [position],
            [AcceptanceSamples.Defect(position.Id, quantity: 15m)]);

        AssertHasError(issues, "defects", "недостаток заявлен на");
    }

    [Fact]
    public void Defects_SumAbovePosition_Reported()
    {
        var position = AcceptanceSamples.Position(quantity: 10m);

        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.AcceptedWithDiscrepancies,
            [position],
            [
                AcceptanceSamples.Defect(position.Id, quantity: 6m),
                AcceptanceSamples.Defect(position.Id, quantity: 6m),
            ]);

        AssertHasError(issues, "defects", "Суммарное количество недостатков");
    }

    [Fact]
    public void Defect_UnknownPosition_Reported()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.AcceptedWithDiscrepancies,
            [AcceptanceSamples.Position()],
            [AcceptanceSamples.Defect(Guid.NewGuid())]);

        AssertHasError(issues, "defects", "позицию, которой нет в документе");
    }

    [Fact]
    public void AcceptedWithDiscrepancies_WithValidDefect_HasNoErrors()
    {
        var position = AcceptanceSamples.Position();

        AssertNoErrors(Validate(
            AcceptanceSamples.Primary(),
            AcceptanceStatus.AcceptedWithDiscrepancies,
            [position],
            [AcceptanceSamples.Defect(position.Id)]));
    }

    // ─── ЕИС ────────────────────────────────────────────────────────────────

    [Fact]
    public void RegisteredInEis_RequiresRegistryNumber()
    {
        var issues = Validate(
            AcceptanceSamples.Primary(eisRegistryNumber: null),
            AcceptanceStatus.RegisteredInEis,
            [AcceptanceSamples.Position()]);

        AssertHasError(issues, "eisRegistryNumber", "реестровый номер контракта");
    }

    // ─── Рабочие дни ────────────────────────────────────────────────────────

    [Fact]
    public void ReviewDeadline_SkipsWeekends()
    {
        // 10.11.2026 — вторник; 10 рабочих дней заканчиваются 24.11.2026.
        var deadline = AcceptanceRules.ReviewDeadline(AcceptanceSamples.DocumentDate, 10);

        Assert.Equal(new DateOnly(2026, 11, 24), deadline);
    }

    [Fact]
    public void AddWorkingDays_FromFriday_MovesToMonday()
    {
        var deadline = AcceptanceCalendar.AddWorkingDays(new DateOnly(2026, 11, 13), 1);

        Assert.Equal(new DateOnly(2026, 11, 16), deadline);
    }

    [Fact]
    public void AddWorkingDays_Zero_ReturnsSameDate()
    {
        var date = new DateOnly(2026, 11, 14);

        Assert.Equal(date, AcceptanceCalendar.AddWorkingDays(date, 0));
    }
}
