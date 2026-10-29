using AeroBag.Application.Alerts;
using AeroBag.Application.Eta;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Domain;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record EtaView(
    DateTimeOffset P10Utc, DateTimeOffset P50Utc, DateTimeOffset P90Utc,
    int P10SecondsFromNow, int P50SecondsFromNow, int P90SecondsFromNow,
    string Basis);

/// <param name="Carousel">Uçuşa atanan bagaj bandı (AODB bağlamı); henüz atanmadıysa null.</param>
/// <param name="Notice">
/// Yolcuya gösterilecek bildirim (W4): NOT_LOADED ya da MISSING_AT_ARRIVAL (açık uyarı; bu durumda ETA verilmez)
/// veya ARRIVAL_DELAYED (indirme beklenenden geç başladı). Yoksa null.
/// </param>
public sealed record PassengerBagView(string BagTag, string FlightId, string State, EtaView? Eta, string? Carousel, string? Notice);

/// <param name="AsOfUtc">
/// ETA'ların göreli hesaplandığı an: operasyon saati (IOperationalClock). Simülasyonda son olay zamanı, gerçek kurulumda gerçek saat.
/// </param>
public sealed record LookupResult(DateTimeOffset AsOfUtc, IReadOnlyList<PassengerBagView> Bags);

public sealed class PassengerQueries(
    NpgsqlDataSource db, LookupKeyHasher hasher, EtaStatistics stats, TimeProvider clock,
    IMlPredictor? ml = null, FlightContextStore? contexts = null, IOperationalClock? operationalClock = null,
    AlertPolicy? alertPolicy = null)
{
    private readonly IOperationalClock _now = operationalClock ?? new EventDerivedClock(db);
    private readonly AlertPolicy _policy = alertPolicy ?? AlertPolicy.Default;

    private record Row(string Tag, string Flight, string State, DateTimeOffset? LastEvent, string? Carousel,
        string? OpenAlert, DateTimeOffset? OnBlock, int FirstBagTargetSec, bool AnyBagUnloaded, DateTimeOffset? UnloadingCompleted);

    /// <summary>
    /// PNR ve soyada ait bavulları döndürür; kayıt yoksa ya da soyad yanlışsa aynı şekilde null.
    /// Havayolları PNR'ları zamanla yeniden kullanır: aynı PNR ve soyad birden çok seyahatle eşleşirse yalnızca en son
    /// uçuş tarihindeki bavullar döner. Böylece PNR'ı bilen biri eski bir seyahatin bavullarını göremez (veri en aza indirme).
    /// </summary>
    public async Task<LookupResult?> LookupAsync(string pnr, string surname, CancellationToken ct = default)
    {
        var key = hasher.Compute(pnr, surname);
        var rows = new List<Row>();

        await using (var cmd = db.CreateCommand(
            """
            SELECT b.bag_tag_number, b.flight_id, b.current_state,
                   (SELECT max(e.occurred_at_utc) FROM baggage_events e
                     WHERE e.bag_id = b.bag_id AND e.to_state = b.current_state AND e.outcome <> 'IGNORED_STALE'),
                   fc.carousel,
                   (SELECT a.kind FROM bag_alerts a WHERE a.bag_id = b.bag_id AND a.closed_at_utc IS NULL
                     ORDER BY a.opened_at_utc DESC LIMIT 1),
                   fc.on_block_utc,
                   f.sla_first_bag_sec,
                   EXISTS (SELECT 1 FROM bags o WHERE o.flight_id = b.flight_id
                            AND o.current_state NOT IN ('CHECKED_IN', 'SECURITY_SCREENED', 'LOADED_ON_AIRCRAFT', 'OFFLOADED_NO_SHOW')),
                   m.unloading_completed_utc
            FROM passengers p
            JOIN bags b ON b.passenger_id = p.passenger_id
            JOIN flights f ON f.flight_id = b.flight_id
            LEFT JOIN flight_context fc ON fc.flight_id = b.flight_id
            LEFT JOIN flight_milestones m ON m.flight_id = b.flight_id
            WHERE p.lookup_key = @k
              AND f.flight_date = (SELECT max(f2.flight_date) FROM passengers p2
                                   JOIN flights f2 ON f2.flight_id = p2.flight_id
                                   WHERE p2.lookup_key = @k)
            ORDER BY b.bag_tag_number
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Bytea) { Value = key });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(
                    r.GetString(0).Trim(), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
                    r.GetInt32(7),
                    r.GetBoolean(8),
                    r.IsDBNull(9) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(9)));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // Operasyon saati yoksa (henüz hiç olay yok) gerçek saate düşülür; o durumda gösterilecek bir ETA da yoktur.
        var asOf = await _now.NowAsync(ct) ?? clock.GetUtcNow();
        var unloadToCarousel = await stats.UnloadToCarouselAsync(ct);
        var chuteToCarousel = await stats.ChuteToCarouselAsync(ct);

        // ML ve fizik tahmini için uçuş bağlamı gerekir. Servis kapalıysa ya da bağlam yoksa eskisi gibi davranılır
        // (iniş öncesinde ETA yok; indirme ve şut aşamasında ampirik ETA).
        var preWindows = new Dictionary<string, EtaWindow?>();
        var bagForecasts = new Dictionary<string, BagDurationForecast?>();
        var ctxs = new Dictionary<string, FlightContext>();
        if (contexts is not null)
        {
            var flightsNeeding = rows
                .Where(r => (int)StateNames.FromDb(r.State)!.Value < (int)BagState.OnCarousel)
                .Select(r => r.Flight).Distinct().ToList();
            ctxs = await contexts.GetAsync(flightsNeeding, ct);

            if (ml is not null)
            {
                foreach (var row in rows)
                {
                    var st = StateNames.FromDb(row.State)!.Value;
                    if (!ctxs.TryGetValue(row.Flight, out var c))
                    {
                        continue;
                    }
                    if ((int)st < (int)BagState.UnloadedAtArrival && !preWindows.ContainsKey(row.Flight))
                    {
                        preWindows[row.Flight] = await ml.PredictAsync(c, ct) is { } f ? MlEtaWindow.Build(c, f, asOf) : null;
                    }
                    else if (st == BagState.UnloadedAtArrival && !bagForecasts.ContainsKey(row.Flight))
                    {
                        bagForecasts[row.Flight] = await ml.PredictBagAsync(c, ct);
                    }
                }
            }
        }

        var bags = rows.Select(row =>
        {
            var state = StateNames.FromDb(row.State)!.Value;
            EtaWindow? window = null;
            if ((int)state < (int)BagState.UnloadedAtArrival)
            {
                preWindows.TryGetValue(row.Flight, out window);
            }
            else if (row.LastEvent is { } last)
            {
                if (state == BagState.UnloadedAtArrival && bagForecasts.TryGetValue(row.Flight, out var bf) && bf is not null)
                {
                    window = MlEtaWindow.BuildForBag(last, bf);
                }
                else if (state == BagState.InboundChute && ctxs.TryGetValue(row.Flight, out var pc))
                {
                    window = PhysicsEtaWindow.Build(last, pc);
                }

                window ??= EtaEstimator.Estimate(state, last, unloadToCarousel, chuteToCarousel);
            }
            // Açık uyarı varsa ETA gösterilmez: "bantta görünecek" demek yanıltıcı olur. Uyarı işçisi birkaç saniyede bir
            // çalıştığı için bavul bu arada okunduysa eski uyarı gösterilmez. NOT_LOADED yolcuya ancak varışta gösterilir
            // (VisibleToPassenger; personel kalkıştan sonra görür).
            string? notice = null;
            if (row.OpenAlert is { } alert && BagAlertRules.VisibleToPassenger(alert, state, row.UnloadingCompleted, asOf, _policy))
            {
                notice = alert;
                window = null;
            }
            else if (BagAlertRules.IsArrivalDelayed(state, row.OnBlock, row.AnyBagUnloaded, row.FirstBagTargetSec, asOf))
            {
                notice = BagNotice.ArrivalDelayed;
            }
            return new PassengerBagView(row.Tag, row.Flight, row.State, window is null ? null : ToView(window, asOf), row.Carousel, notice);
        }).ToList();

        return new LookupResult(asOf, bags);
    }

    private static EtaView ToView(EtaWindow w, DateTimeOffset asOf)
    {
        static int Secs(DateTimeOffset t, DateTimeOffset now) => (int)Math.Max(0, Math.Round((t - now).TotalSeconds));
        return new EtaView(w.P10Utc, w.P50Utc, w.P90Utc,
            Secs(w.P10Utc, asOf), Secs(w.P50Utc, asOf), Secs(w.P90Utc, asOf), w.Basis);
    }
}
