using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>Süresi dolan çıkış kayıtlarını periyodik işler. Servis yeniden başlasa da bekleyen kayıtlar veritabanında durur.</summary>
public sealed class ExitDeadlineBackgroundService(ExitDeadlineService service, ILogger<ExitDeadlineBackgroundService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int n;
                do
                {
                    n = await service.ProcessDueAsync(100, stoppingToken);
                    if (n > 0)
                    {
                        log.LogInformation("{Count} bagaj için çıkış süresi doldu (çıkış okunmadı).", n);
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
                log.LogError(ex, "Çıkış süresi döngüsü hata verdi.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
