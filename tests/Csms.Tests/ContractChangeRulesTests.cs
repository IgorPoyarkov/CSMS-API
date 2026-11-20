using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Правила ст. 95 44-ФЗ: что считается корректным изменением или расторжением
/// и какие основания допускают какие правки. Проверки вызываются доменом при
/// каждом переходе, поэтому здесь фиксируется именно правовая логика, а не
/// поведение формы.
/// </summary>
public class ContractChangeRulesTests
{
    private static IReadOnlyList<ContractChangeIssue> Validate(
        ContractChangeInput input,
        ContractChangeStatus status = ContractChangeStatus.Draft,
        params ContractChangeDocument[] documents) =>
        ContractChangeRules.Validate(input, documents, status);

    private static void AssertHasError(
        IReadOnlyList<ContractChangeIssue> issues,
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

    private static void AssertNoErrors(IReadOnlyList<ContractChangeIssue> issues) =>
        Assert.True(
            issues.All(issue => !issue.IsError),
            $"Ошибок быть не должно: {Describe(issues)}");

    private static string Describe(IEnumerable<ContractChangeIssue> issues) =>
        string.Join(" | ", issues.Select(issue => $"{issue.Severity}/{issue.Field}: {issue.Message}"));

    // ─── Корректные документы ───────────────────────────────────────────────

    [Fact]
    public void Draft_ValidModification_HasNoErrors()
    {
        AssertNoErrors(Validate(ContractChangeSamples.Modification()));
    }

    [Fact]
    public void Draft_ValidTermination_HasNoErrors()
    {
        AssertNoErrors(Validate(ContractChangeSamples.Termination()));
    }

    [Fact]
    public void Draft_DoesNotRequireDocumentsYet()
    {
        // Черновик можно сохранить до формирования соглашения: комплектность
        // проверяется при отправке на согласование, а не при каждом вводе.
        var issues = Validate(ContractChangeSamples.Modification());

        Assert.DoesNotContain(issues, issue => issue.IsError && issue.Field == "documents");
    }

    // ─── Общие реквизиты ────────────────────────────────────────────────────

    [Fact]
    public void MissingContractFields_Reported()
    {
        var input = ContractChangeSamples.Modification() with
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
    public void ClosedContract_CannotBeChanged(string status)
    {
        var issues = Validate(ContractChangeSamples.Modification(contractStatus: status));

        AssertHasError(issues, "contractStatus", "завершён или расторгнут");
    }

    [Fact]
    public void ContractInDraftStatus_CannotBeChanged()
    {
        var issues = Validate(ContractChangeSamples.Modification(contractStatus: "draft"));

        AssertHasError(issues, "contractStatus", "только заключённый или исполняемый");
    }

    [Fact]
    public void UnknownBasis_Reported()
    {
        var issues = Validate(ContractChangeSamples.Modification(basisCode: "no_such_basis"));

        AssertHasError(issues, "basisCode", "Неизвестное основание");
    }

    [Fact]
    public void BasisOfAnotherKind_Reported()
    {
        // Основание расторжения нельзя применить к изменению: иначе можно было
        // бы «расторгнуть» контракт, оставив его в исполнении.
        var issues = Validate(ContractChangeSamples.Modification(basisCode: "by_agreement"));

        AssertHasError(issues, "basisCode", "не соответствует виду документа");
    }

    [Fact]
    public void ReasonTooShort_Reported()
    {
        var issues = Validate(ContractChangeSamples.Modification(reason: "мало"));

        AssertHasError(issues, "reason", "не менее 10 символов");
    }

    [Fact]
    public void ChangeDateBeforeContractDate_Reported()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(changeDate: new DateOnly(2026, 8, 1)));

        AssertHasError(issues, "changeDate", "раньше даты заключения контракта");
    }

    // ─── Изменение контракта ────────────────────────────────────────────────

    [Fact]
    public void PriceReduction_RejectsPriceIncrease()
    {
        var issues = Validate(ContractChangeSamples.Modification(newPrice: 110_000m));

        AssertHasError(issues, "newPrice", "только снижаться");
    }

    [Fact]
    public void PriceReduction_RejectsSubjectChange()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(newSubject: "Поставка иного оборудования"));

        AssertHasError(issues, "newSubject", "Предмет контракта по этому основанию не меняется");
    }

    [Fact]
    public void QuantityChange_AboveTenPercent_Rejected()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(basisCode: "quantity_10", newQuantity: 115m));

        AssertHasError(issues, "newQuantity", "не более чем на 10 %");
    }

    [Fact]
    public void QuantityChange_WithinTenPercent_Accepted()
    {
        AssertNoErrors(Validate(
            ContractChangeSamples.Modification(
                basisCode: "quantity_10",
                newQuantity: 105m,
                newPrice: 105_000m)));
    }

    [Fact]
    public void QuantityChange_RejectsPriceGrowthAboveLimit()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(
                basisCode: "quantity_10",
                newQuantity: 105m,
                newPrice: 120_000m));

        AssertHasError(issues, "newPrice", "не может превышать 10 %");
    }

    [Fact]
    public void QuantityChange_WithoutContractQuantity_WarnsInsteadOfGuessing()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(
                basisCode: "quantity_10",
                newQuantity: 105m,
                contractQuantity: null));

        AssertNoErrors(issues);
        Assert.Contains(issues, issue =>
            !issue.IsError && issue.Field == "contractQuantity" && issue.Message.Contains("лимит 10 %"));
    }

    [Fact]
    public void GovernmentAct_RequiresActNumberAndDate()
    {
        var issues = Validate(ContractChangeSamples.Modification(basisCode: "government_act"));

        AssertHasError(issues, "governmentAct", "акта органа власти");
    }

    [Fact]
    public void GovernmentAct_AllowsExecutionDateChange()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(
                basisCode: "government_act",
                governmentActNumber: "12-р",
                governmentActDate: new DateOnly(2026, 10, 15),
                newExecutionEndDate: new DateOnly(2027, 3, 1)));

        AssertNoErrors(issues);
    }

    [Fact]
    public void ExecutionDateChange_WithoutGovernmentAct_Rejected()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(newExecutionEndDate: new DateOnly(2027, 3, 1)));

        AssertHasError(issues, "newExecutionEndDate", "изменение срока допускается по акту");
    }

    [Fact]
    public void PriceFormula_RequiresFormula()
    {
        var issues = Validate(ContractChangeSamples.Modification(basisCode: "price_formula"));

        AssertHasError(issues, "formulaText", "укажите формулу");
    }

    [Fact]
    public void UnitPrice_RequiresValue()
    {
        var issues = Validate(ContractChangeSamples.Modification(basisCode: "unit_price"));

        AssertHasError(issues, "unitPrice", "цену единицы");
    }

    [Fact]
    public void UnitPrice_CannotExceedInitialMaxPrice()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(
                basisCode: "unit_price",
                unitPrice: 500m,
                initialMaxPrice: 400m));

        AssertHasError(issues, "unitPrice", "начальную максимальную цену");
    }

    [Fact]
    public void SupplierReplacement_RequiresNewSupplier()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(basisCode: "supplier_replacement", newPrice: null));

        AssertHasError(issues, "newSupplier", "нового поставщика");
    }

    [Fact]
    public void QualityImprovement_RejectsPriceIncrease()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(
                basisCode: "quality_improvement",
                newPrice: 110_000m,
                newSubject: "Оборудование с улучшенными характеристиками"));

        AssertHasError(issues, "newPrice", "увеличению цены");
    }

    // ─── Расторжение контракта ──────────────────────────────────────────────

    [Fact]
    public void Termination_RequiresTerminationDate()
    {
        var input = ContractChangeSamples.Termination() with { TerminationDate = null };

        AssertHasError(Validate(input), "terminationDate", "Укажите дату расторжения");
    }

    [Fact]
    public void TerminationDateBeforeChangeDate_Rejected()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(terminationDate: new DateOnly(2026, 10, 20)));

        AssertHasError(issues, "terminationDate", "не может быть раньше даты документа");
    }

    [Fact]
    public void CourtDecision_RequiresNumberAndDate()
    {
        var issues = Validate(ContractChangeSamples.Termination(basisCode: "court_decision"));

        AssertHasError(issues, "courtDecision", "судебного акта");
    }

    [Fact]
    public void CourtDecision_TerminationBeforeDecision_Rejected()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(
                basisCode: "court_decision",
                courtDecisionNumber: "А40-1/2026",
                courtDecisionDate: new DateOnly(2026, 11, 10),
                terminationDate: new DateOnly(2026, 11, 5)));

        AssertHasError(issues, "terminationDate", "раньше даты судебного акта");
    }

    [Fact]
    public void UnilateralTermination_RequiresNotice()
    {
        var issues = Validate(ContractChangeSamples.Termination(basisCode: "customer_unilateral"));

        AssertHasError(issues, "notice", "уведомления об одностороннем отказе");
    }

    [Fact]
    public void UnilateralTermination_RespectsTenDayPeriod()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(
                basisCode: "customer_unilateral",
                noticeNumber: "У-15",
                noticeDate: new DateOnly(2026, 11, 5),
                terminationDate: new DateOnly(2026, 11, 10)));

        AssertHasError(issues, "terminationDate", "не ранее чем через 10 дней");
        Assert.Contains(issues, issue =>
            issue.IsError && issue.Field == "terminationDate" && issue.Message.Contains("15.11.2026"));
    }

    [Fact]
    public void UnilateralTermination_OnTenthDay_Accepted()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(
                basisCode: "supplier_unilateral",
                noticeNumber: "У-16",
                noticeDate: new DateOnly(2026, 11, 5),
                terminationDate: new DateOnly(2026, 11, 15)));

        AssertNoErrors(issues);
    }

    [Fact]
    public void Termination_PriceChangeIsWarningOnly()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(newPrice: 120_000m));

        AssertNoErrors(issues);
        Assert.Contains(issues, issue =>
            !issue.IsError && issue.Field == "newPrice" && issue.Message.Contains("цена контракта не меняется"));
    }

    // ─── Комплектность и ЕИС ────────────────────────────────────────────────

    [Fact]
    public void OnApproval_RequiresAgreement()
    {
        var issues = Validate(
            ContractChangeSamples.Modification(),
            ContractChangeStatus.OnApproval);

        AssertHasError(issues, "documents", "«Соглашение»");
    }

    [Fact]
    public void OnApproval_WithAgreement_HasNoErrors()
    {
        AssertNoErrors(Validate(
            ContractChangeSamples.Modification(),
            ContractChangeStatus.OnApproval,
            ContractChangeSamples.Document()));
    }

    [Fact]
    public void OnApproval_AcceptsDecisionInsteadOfNotice()
    {
        // По одностороннему отказу решение заказчика равнозначно уведомлению:
        // закон требует довести решение до стороны, а не конкретный бланк.
        var issues = Validate(
            ContractChangeSamples.Termination(
                basisCode: "customer_unilateral",
                noticeNumber: "У-17",
                noticeDate: new DateOnly(2026, 11, 5)),
            ContractChangeStatus.OnApproval,
            ContractChangeSamples.Document(ContractChangeDocumentType.Decision));

        AssertNoErrors(issues);
    }

    [Fact]
    public void RegisteredInEis_RequiresRegistryNumber()
    {
        var issues = Validate(
            ContractChangeSamples.Termination(eisRegistryNumber: null),
            ContractChangeStatus.RegisteredInEis,
            ContractChangeSamples.Document());

        AssertHasError(issues, "eisRegistryNumber", "реестровый номер контракта");
    }

    [Fact]
    public void RegisteredInEis_WithRegistryNumber_HasNoErrors()
    {
        AssertNoErrors(Validate(
            ContractChangeSamples.Termination(),
            ContractChangeStatus.RegisteredInEis,
            ContractChangeSamples.Document()));
    }

    [Fact]
    public void DocumentChangedAfterSave_ReportedAsError()
    {
        var original = ContractChangeSamples.Document();
        var tampered = ContractChangeDocument.Restore(
            original.Id,
            original.Type,
            original.Title,
            "Подменённый после согласования текст",
            original.Version,
            original.Sha256,
            original.CreatedBy,
            original.CreatedAt);

        var issues = Validate(ContractChangeSamples.Modification(), ContractChangeStatus.Draft, tampered);

        AssertHasError(issues, "documents", "хеш содержимого не совпадает");
    }
}
