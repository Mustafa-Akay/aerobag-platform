using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;

namespace AeroBag.Api;

/// <summary>
/// Biten uçuşların gerçek ilk/son bavul sürelerini ML servisine bildirir; servis bunlardan canlı düzeltme hesaplar.
/// ML kapalıysa hiçbir şey yapmaz. Servis cevap vermezse uçuş işaretlenmez ve sonraki turda yeniden denenir.
/// </summary>
public sealed class MlOutcomeBackgroundService(
    FlightContextStore store, IMlPredictor ml, IOperationalClock clock, ILogger<MlOutcomeBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private const int BagBatch = 200;

    /// <summary>Banta gelmiş bavulların gerçek sürelerini bildirir. Servis kabul edemeyecek sürelerdekiler (>2 saat) gönderilmeden kapatılır.</summary>
    private async Task ReportBagOutcomesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pending = await store.PendingBagOutcomesAsync(BagBatch, ct);
            if (pending.Count == 0)
            {
                return;
            }

            var valid = pending.Where(p => p.Outcome.DurationSec is > 0 and <= 7200).ToList();
            var recorded = await ml.ReportBagOutcomesAsync(valid.Select(p => p.Outcome).ToList(), ct);
            if (recorded < 0)
            {
                return; // servis cevap vermedi: işaretleme, sonraki turda yeniden dene
            }

            await store.MarkBagOutcomesReportedAsync(pending.Select(p => p.BagId).ToList(), ct);
            if (pending.Count < BagBatch)
            {
                return;
            }
        }
    }

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
                await ReportBagOutcomesAsync(stoppingToken);

                var now = await clock.NowAsync(stoppingToken);
                var pending = now is null ? new List<PendingOutcome>() : await store.PendingOutcomesAsync(now.Value, stoppingToken);
                foreach (var p in pending)
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
