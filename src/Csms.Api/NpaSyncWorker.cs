using Csms.Infrastructure;
using Csms.Infrastructure.Npa;

namespace Csms.Api;

/// <summary>
/// Фоновая проверка официальных публикаций. Раз в Npa:SyncIntervalHours агент
/// скачивает текст каждого акта, сравнивает SHA-256 и пересобирает правила
/// только если публикация изменилась. Ручное вмешательство не требуется.
/// </summary>
public sealed class NpaSyncWorker(
    NpaOptions options,
    NpaSyncService sync,
    ILogger<NpaSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || options.Documents.Count == 0)
        {
            logger.LogInformation("Агент разметки НПА выключен или список актов пуст.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.SyncIntervalHours));

        do
        {
            foreach (var document in options.Documents)
            {
                try
                {
                    var result = await sync.SyncAsync(document.Code, cancellationToken: stoppingToken);
                    logger.LogInformation(
                        "Акт {Code}: {Status} ({Version}), правил {Rules}",
                        result.Code,
                        result.Status,
                        result.Version,
                        result.RuleCount);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // Один недоступный акт не должен останавливать остальные.
                    logger.LogError(exception, "Акт {Code}: обновление не удалось", document.Code);
                }
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
