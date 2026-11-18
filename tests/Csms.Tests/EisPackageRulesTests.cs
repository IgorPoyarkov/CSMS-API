using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Правила домена: их нельзя обойти, даже если вызывающий код ошибётся.
/// Поэтому проверяются именно отказы — неверный номер, чужая подпись,
/// попытка отправить неподписанное.
/// </summary>
public sealed class EisPackageRulesTests
{
    private const string ContractNumber = "0373100000123000001";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static EisPackage NewPackage(string number = ContractNumber) =>
        EisPackage.Create(
            EisDocumentType.SmpAttraction,
            number,
            "2612345678901234567890",
            "<eisDraft/>",
            "placeholder-v0",
            Now);

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("037310000012300000X")]
    [InlineData("03731000001230000012")]
    public void Создание_с_неверным_номером_контракта_отклоняется(string number)
    {
        var error = Assert.Throws<DomainException>(() => NewPackage(number));

        Assert.Contains("19 цифр", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Создание_с_пустым_содержимым_отклоняется()
    {
        Assert.Throws<DomainException>(() =>
            EisPackage.Create(EisDocumentType.SmpAttraction, ContractNumber, "икз", "  ", "placeholder-v0", Now));
    }

    [Fact]
    public void Отправка_без_подписи_невозможна()
    {
        var package = NewPackage();

        var error = Assert.Throws<DomainException>(() => package.MarkSent("eis-guid", Now));

        Assert.Equal(EisPackageStatus.Draft, package.Status);
        Assert.Contains("«Подписан»", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Подпись_под_другим_содержимым_отклоняется()
    {
        var package = NewPackage();
        package.BeginSigning(Now);

        var error = Assert.Throws<DomainException>(() =>
            package.AttachSignature("AAAA", "thumbprint", new string('0', 64), Now));

        Assert.Equal(EisPackageStatus.Signing, package.Status);
        Assert.Contains("другим содержимым", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Подпись_под_своим_содержимым_принимается_и_даёт_статус_Signed()
    {
        var package = NewPackage();
        package.BeginSigning(Now);

        package.AttachSignature("AAAA", "thumbprint", package.PayloadSha256.ToUpperInvariant(), Now);

        Assert.Equal(EisPackageStatus.Signed, package.Status);
        Assert.Equal("AAAA", package.SignatureBase64);
        Assert.Equal(Now, package.SignedAt);
    }

    [Fact]
    public void После_подписания_содержимое_менять_нельзя()
    {
        var package = NewPackage();
        package.BeginSigning(Now);
        package.AttachSignature("AAAA", "thumbprint", null, Now);

        Assert.Throws<DomainException>(() => package.UpdatePayload("<eisDraft/>", "placeholder-v0", Now));
    }

    [Fact]
    public void Приём_без_проверки_ФЛК_невозможен()
    {
        var package = NewPackage();
        package.BeginSigning(Now);
        package.AttachSignature("AAAA", "thumbprint", null, Now);
        package.MarkSent("eis-guid", Now);

        Assert.Throws<DomainException>(() => package.MarkAccepted(Now));
    }

    [Fact]
    public void Отказ_ФЛК_без_описания_не_остаётся_пустым()
    {
        var package = NewPackage();
        package.BeginSigning(Now);
        package.AttachSignature("AAAA", "thumbprint", null, Now);
        package.MarkSent("eis-guid", Now);
        package.MarkFlkChecking(Now);

        package.MarkRejected([], Now);

        Assert.Equal(EisPackageStatus.RejectedByFlk, package.Status);
        Assert.Single(package.FlkErrors);
        Assert.Equal("FLK_UNKNOWN", package.FlkErrors[0].Code);
    }

    [Fact]
    public void Возврат_в_работу_снимает_подпись_и_ошибки()
    {
        var package = NewPackage();
        package.BeginSigning(Now);
        package.AttachSignature("AAAA", "thumbprint", null, Now);
        package.MarkSent("eis-guid", Now);
        package.MarkFlkChecking(Now);
        package.MarkRejected([new FlkError("FLK-101", "нет поля")], Now);

        package.Revise(Now);

        Assert.Equal(EisPackageStatus.Draft, package.Status);
        Assert.Null(package.SignatureBase64);
        Assert.Empty(package.FlkErrors);
    }

    [Fact]
    public void Каждый_переход_попадает_в_журнал()
    {
        var package = NewPackage();
        package.BeginSigning(Now);
        package.AttachSignature("AAAA", "thumbprint", null, Now);

        // создание + начало подписания + приложенная подпись
        Assert.Equal(3, package.History.Count);
        Assert.Equal(EisPackageStatus.Signing, package.History[^1].From);
        Assert.Equal(EisPackageStatus.Signed, package.History[^1].To);
    }
}
