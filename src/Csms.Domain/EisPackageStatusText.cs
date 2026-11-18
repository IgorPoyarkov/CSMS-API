namespace Csms.Domain;

/// <summary>
/// Русские названия статусов для сообщений об ошибках.
///
/// Нужны потому, что эти сообщения показываются человеку, а не только пишутся
/// в лог: «пакет в статусе «Черновик»» читается, а «пакет в статусе Draft» —
/// нет. Технические имена остаются в API и в логах.
/// </summary>
public static class EisPackageStatusText
{
    public static string Ru(this EisPackageStatus status) =>
        status switch
        {
            EisPackageStatus.Draft => "Черновик",
            EisPackageStatus.Signing => "Подписание",
            EisPackageStatus.Signed => "Подписан",
            EisPackageStatus.SentToEis => "Отправлен в ЕИС",
            EisPackageStatus.FlkChecking => "Проверка ФЛК",
            EisPackageStatus.AcceptedInLk => "Принят ЕИС",
            EisPackageStatus.RejectedByFlk => "Отклонён ФЛК",
            EisPackageStatus.SendFailed => "Ошибка отправки",
            _ => status.ToString(),
        };
}
