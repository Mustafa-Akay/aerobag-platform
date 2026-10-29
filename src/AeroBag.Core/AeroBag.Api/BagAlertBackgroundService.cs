using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>Kayıp bavul uyarılarını periyodik değerlendirir (Faz 6, W4). Durum veritabanında tutulur; yeniden başlatma bir şey kaybettirmez.</summary>
public sealed class BagAlertBackgroundService(BagAlertService service, ILogger<BagAlertBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var r = await service.ScanAsync(stoppingToken);
                if (r.Opened > 0 || r.Closed > 0)
                {
                    log.LogInformation("Bavul uyarıları: {Opened} açıldı, {Closed} kapandı.", r.Opened, r.Closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Bavul uyarısı döngüsü hata verdi.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
