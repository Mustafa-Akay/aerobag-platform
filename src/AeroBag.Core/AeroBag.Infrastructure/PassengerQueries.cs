using AeroBag.Application.Eta;
using AeroBag.Application.Ml;
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
/// ETA'ların göreli hesaplandığı an. Simülasyonda "şimdi", son olay zamanıdır (sanal saat); gerçek kurulumda gerçek saat olur.
/// </param>
public sealed record LookupResult(DateTimeOffset AsOfUtc, IReadOnlyList<PassengerBagView> Bags);

public sealed class PassengerQueries(
    NpgsqlDataSource db, LookupKeyHasher hasher, EtaStatistics stats, TimeProvider clock,
    IMlPredictor? ml = null, FlightContextStore? contexts = null)
{
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

        var asOf = await MaxEventTimeAsync(ct) ?? clock.GetUtcNow();
        var unloadToCarousel = await stats.UnloadToCarouselAsync(ct);
        var chuteToCarousel = await stats.ChuteToCarouselAsync(ct);

        // İniş öncesi bavullar için ampirik ETA yoktur; uçuş bağlamı ve ML servisi varsa uçuş düzeyinde tahmin kullanılır.
        // Servis kapalıysa ya da bağlam yoksa eskisi gibi ETA gösterilmez (taban çizgisine düşüş).
        var mlByFlight = new Dictionary<string, EtaWindow?>();
        if (ml is not null && contexts is not null)
        {
            var preFlights = rows
                .Where(r => (int)StateNames.FromDb(r.State)!.Value < (int)BagState.UnloadedAtArrival)
                .Select(r => r.Flight).Distinct().ToList();
            var ctxs = await contexts.GetAsync(preFlights, ct);
            foreach (var flightId in preFlights)
            {
                EtaWindow? w = null;
                if (ctxs.TryGetValue(flightId, out var c) && await ml.PredictAsync(c, ct) is { } forecast)
                {
                    w = MlEtaWindow.Build(c, forecast, asOf);
                }
                mlByFlight[flightId] = w;
            }
        }

        var bags = rows.Select(row =>
        {
            var state = StateNames.FromDb(row.State)!.Value;
            var window = row.LastEvent is { } last
                ? EtaEstimator.Estimate(state, last, unloadToCarousel, chuteToCarousel)
                : null;
            if (window is null && mlByFlight.TryGetValue(row.Flight, out var mlWindow))
            {
                window = mlWindow;
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

    private async Task<DateTimeOffset?> MaxEventTimeAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            // BSM mesajı zaman taşımaz (olay zamanı = alınma anı), bu yüzden sanal saati bozmasın diye dışarıda bırakılır.
            "SELECT max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM'");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0);
    }
}
