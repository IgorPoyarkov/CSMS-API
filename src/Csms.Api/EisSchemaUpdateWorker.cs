using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>
/// Фоновая проверка Альбома форматов ЕИС. Первый запуск — сразу после старта,
/// дальше раз в <c>CheckIntervalMinutes</c>. Ошибки сети не останавливают
/// службу: активные XSD остаются прежними, 상태 видно через API.
/// </summary>
public sealed class EisSchemaUpdateWorker(
    EisSchemaDownloader downloader,
    EisSchemaSourceOptions options,
    ILogger<EisSchemaUpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Автообновление XSD ЕИС отключено настройкой");

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
                    logger.LogWarning("Проверка XSD ЕИС завершилась ошибкой: {Error}", snapshot.LastError);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Непредвиденная ошибка проверки обновлений XSD ЕИС");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
