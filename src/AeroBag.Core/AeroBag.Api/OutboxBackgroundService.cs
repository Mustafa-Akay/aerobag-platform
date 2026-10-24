using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>Outbox'ı sürekli boşaltır: iş yoksa kısa bekler, hata olursa daha uzun bekler.</summary>
public sealed class OutboxBackgroundService(OutboxRelay relay, IOutboxPublisher publisher, ILogger<OutboxBackgroundService> log)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleDelay;
            try
            {
                var result = await relay.DispatchOnceAsync(publisher, 100, stoppingToken);
                if (result.Failed > 0)
                {
                    log.LogWarning("Outbox: {Failed} mesaj gönderilemedi, yeniden denenecek.", result.Failed);
                }
                if (result.Published > 0 || result.Failed > 0)
                {
                    delay = TimeSpan.Zero;
                    if (result.Failed > 0) delay = ErrorDelay;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox döngüsü hata verdi.");
                delay = ErrorDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
