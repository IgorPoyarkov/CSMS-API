using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>
/// Фоновая проверка публичной выгрузки ст. 19.28 КоАП. Первый запуск — сразу
/// после старта, дальше раз в сутки. Сетевые ошибки не останавливают службу:
/// активный снимок остаётся прежним, подробности видны через API статуса.
/// </summary>
public sealed class EisCounterpartyUpdateWorker(
    EisCounterpartyDownloader downloader,
    EisCounterpartySourceOptions options,
    ILogger<EisCounterpartyUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Проверка выгрузки ст. 19.28 КоАП отключена настройкой");

            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.CheckIntervalMinutes));

        do
        {
            try
            {
                var snapshot = await downloader.CheckAndInstallAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (snapshot.State == "Error")
                {
                    logger.LogWarning(
                        "Проверка выгрузки ст. 19.28 КоАП завершилась ошибкой: {Error}",
                        snapshot.LastError);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Непредвиденная ошибка проверки выгрузки ст. 19.28 КоАП");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
