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

public sealed record PassengerBagView(string BagTag, string FlightId, string State, EtaView? Eta);

/// <param name="AsOfUtc">
/// ETA'ların göreli hesaplandığı an: operasyon saati (IOperationalClock). Simülasyonda son olay zamanı, gerçek kurulumda gerçek saat.
/// </param>
public sealed record LookupResult(DateTimeOffset AsOfUtc, IReadOnlyList<PassengerBagView> Bags);

public sealed class PassengerQueries(
    NpgsqlDataSource db, LookupKeyHasher hasher, EtaStatistics stats, TimeProvider clock,
    IMlPredictor? ml = null, FlightContextStore? contexts = null, IOperationalClock? operationalClock = null)
{
    private readonly IOperationalClock _now = operationalClock ?? new EventDerivedClock(db);

    private record Row(string Tag, string Flight, string State, DateTimeOffset? LastEvent);

    /// <summary>PNR ve soyada ait bavulları döndürür; kayıt yoksa ya da soyad yanlışsa aynı şekilde null.</summary>
    public async Task<LookupResult?> LookupAsync(string pnr, string surname, CancellationToken ct = default)
    {
        var key = hasher.Compute(pnr, surname);
        var rows = new List<Row>();

        await using (var cmd = db.CreateCommand(
            """
            SELECT b.bag_tag_number, b.flight_id, b.current_state,
                   (SELECT max(e.occurred_at_utc) FROM baggage_events e
                     WHERE e.bag_id = b.bag_id AND e.to_state = b.current_state AND e.outcome <> 'IGNORED_STALE')
            FROM passengers p
            JOIN bags b ON b.passenger_id = p.passenger_id
            WHERE p.lookup_key = @k
            ORDER BY b.bag_tag_number
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Bytea) { Value = key });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(
                    r.GetString(0).Trim(), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3)));
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
            return new PassengerBagView(row.Tag, row.Flight, row.State, window is null ? null : ToView(window, asOf));
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
