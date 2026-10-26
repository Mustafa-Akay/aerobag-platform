using AeroBag.Application.Ml;
using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>
/// Biten uçuşların gerçek ilk/son bavul sürelerini ML servisine bildirir; servis bunlardan canlı düzeltme hesaplar.
/// ML kapalıysa hiçbir şey yapmaz. Servis cevap vermezse uçuş işaretlenmez ve sonraki turda yeniden denenir.
/// </summary>
public sealed class MlOutcomeBackgroundService(
    FlightContextStore store, IMlPredictor ml, ILogger<MlOutcomeBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ml is NullMlPredictor)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var p in await store.PendingOutcomesAsync(stoppingToken))
                {
                    // Tahmin önce istenir: servis, düzeltme hesabı için ham tahmini uçuş numarasıyla saklar.
                    if (await ml.PredictAsync(p.Context, stoppingToken) is null)
                    {
                        break;
                    }
                    if (await ml.ReportOutcomeAsync(p.Context.FlightId, p.FirstMin, p.LastMin, stoppingToken))
                    {
                        await store.MarkReportedAsync(p.Context.FlightId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ML sonuç bildirimi döngüsü hata verdi.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
