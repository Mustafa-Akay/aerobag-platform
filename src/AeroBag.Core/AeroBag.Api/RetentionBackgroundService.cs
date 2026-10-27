using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>Saklama süresi işini saatte bir çalıştırır (gerçek saat; simülasyonun sanal saati kullanılmaz).</summary>
public sealed class RetentionBackgroundService(RetentionService service, ILogger<RetentionBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int n;
                do
                {
                    n = await service.AnonymizeDueAsync(500, stoppingToken);
                    if (n > 0)
                    {
                        log.LogInformation("{Count} yolcunun arama özeti saklama süresi dolduğu için değiştirildi.", n);
                    }
                }
                while (n > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Saklama süresi işi hata verdi.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
