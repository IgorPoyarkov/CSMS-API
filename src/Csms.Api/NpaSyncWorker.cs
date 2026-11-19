using Csms.Infrastructure;
using Csms.Infrastructure.Npa;

namespace Csms.Api;

/// <summary>
/// Фоновая проверка официальных публикаций. Раз в сутки в Npa:CheckTime
/// (московское время, UTC+3) агент скачивает текст каждого акта, сравнивает
/// SHA-256 и пересобирает правила только если публикация изменилась.
///
/// Акты идут строго последовательно: следующий начинается только после
/// завершения предыдущего и паузы Npa:CheckDelayMinutes. Три тяжёлых OCR-прогона
/// не конкурируют за CPU и не нагружают портал публикования одновременно.
/// </summary>
public sealed class NpaSyncWorker(
    NpaOptions options,
    NpaSyncService sync,
    ILogger<NpaSyncWorker> logger) : BackgroundService
{
    // В России с 2011 года нет сезонного времени: Москва всегда UTC+3.
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || options.Documents.Count == 0)
        {
            logger.LogInformation("Агент разметки НПА выключен или список актов пуст.");
            return;
        }

        // Validate() в Program гарантирует, что время разбирается.
        options.TryGetCheckTime(out var checkTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntil(checkTime);
            logger.LogInformation(
                "Следующая проверка НПА: {CheckTime} МСК, через {Delay}",
                checkTime.ToString("HH:mm"),
                delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        for (var index = 0; index < options.Documents.Count; index++)
        {
            // Пауза только между актами: после последнего ждём следующего дня.
            if (index > 0)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMinutes(options.CheckDelayMinutes),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var document = options.Documents[index];
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
                // Один недоступный акт не останавливает остальные. Время неудачной
                // проверки NpaSyncService уже записал в хранилище.
                logger.LogError(exception, "Акт {Code}: обновление не удалось", document.Code);
            }
        }
    }

    /// <summary>Задержка до ближайшего времени проверки по московскому времени.</summary>
    private TimeSpan DelayUntil(TimeOnly checkTime)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(MoscowOffset);
        var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, MoscowOffset);
        var next = midnight.Add(checkTime.ToTimeSpan());

        if (next <= now)
        {
            next = next.AddDays(1);
        }

        return next - now;
    }
}
