using AeroBag.Application.Ml;
using AeroBag.Application.Sla;
using AeroBag.Application.Time;
using Npgsql;

namespace AeroBag.Infrastructure;

/// <summary>
/// Uçuş başına bant teslim SLA'sı (ARCHITECTURE.md 6.2). Hedefler flights tablosundaki DEMO varsayımlarıdır.
/// Süreler on-block'tan ölçülür. "Şimdi" operasyon saatinden gelir (IOperationalClock: gerçek ya da simülasyon saati).
/// Gerçekleşen ilk bavul ilk banta geliş olayıdır; son bavul ise yalnızca uçuştaki tüm bavullar banta
/// (ya da sonrasına) geçtiyse gerçekleşmiş sayılır. Ondan önce son bavul için model tahmini kullanılır.
/// ML servisi kapalıysa tahmin yoktur: gerçekleşen ya da süresi dolmuş durumlar yine raporlanır, gerisi UNKNOWN olur.
/// </summary>
public sealed record FlightSla(
    string FlightId,
    string Assumption,
    DateTimeOffset? OnBlockUtc,
    double? ElapsedSec,
    bool AllBagsArrived,
    SlaLeg FirstBag,
    SlaLeg LastBag);

public sealed class SlaQueries(NpgsqlDataSource db, IMlPredictor ml, IOperationalClock? clock = null)
{
    private readonly IOperationalClock _now = clock ?? new EventDerivedClock(db);

    public const string Assumption = "DEMO_ASSUMPTION";

    private sealed record Row(string FlightId, int FirstTarget, int LastTarget, int Total, int NotArrived,
        DateTimeOffset? FirstCarousel, DateTimeOffset? LastCarousel);

    public async Task<IReadOnlyList<FlightSla>> GetAsync(CancellationToken ct = default)
    {
        var now = await _now.NowAsync(ct);

        var rows = new List<Row>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT f.flight_id, f.sla_first_bag_sec, f.sla_last_bag_sec,
                   (SELECT count(*)::int FROM bags b WHERE b.flight_id = f.flight_id),
                   (SELECT count(*)::int FROM bags b WHERE b.flight_id = f.flight_id
                      AND b.current_state IN ('CHECKED_IN','SECURITY_SCREENED','LOADED_ON_AIRCRAFT','UNLOADED_AT_ARRIVAL','INBOUND_CHUTE')),
                   x.first_t, x.last_t
            FROM flights f
            LEFT JOIN LATERAL (
                SELECT min(c.t) AS first_t, max(c.t) AS last_t
                FROM (
                    SELECT min(e.occurred_at_utc) AS t
                    FROM bags b JOIN baggage_events e ON e.bag_id = b.bag_id
                    WHERE b.flight_id = f.flight_id AND e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'
                    GROUP BY b.bag_id
                ) c
            ) x ON TRUE
            ORDER BY f.flight_id
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                    r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        var contexts = await new FlightContextStore(db).GetAsync(rows.Select(x => x.FlightId).ToList(), ct);
        var result = new List<FlightSla>();
        foreach (var row in rows)
        {
            contexts.TryGetValue(row.FlightId, out var ctx);
            var onBlock = ctx?.OnBlockUtc;
            if (onBlock is null || now is null)
            {
                var empty = new SlaLeg(row.FirstTarget, null, null, SlaStatus.Unknown, null, null, null, null);
                result.Add(new FlightSla(row.FlightId, Assumption, onBlock, null, false, empty, empty with { TargetSec = row.LastTarget }));
                continue;
            }

            var elapsed = Math.Max((now.Value - onBlock.Value).TotalSeconds, 0);
            var allArrived = row.Total > 0 && row.NotArrived == 0;
            double? firstActual = row.FirstCarousel is { } f ? (f - onBlock.Value).TotalSeconds : null;
            double? lastActual = allArrived && row.LastCarousel is { } l ? (l - onBlock.Value).TotalSeconds : null;
            var forecast = firstActual is null || lastActual is null ? await ml.PredictAsync(ctx!, ct) : null;

            var first = SlaRisk.Evaluate(row.FirstTarget, firstActual, elapsed, forecast?.First);
            var last = SlaRisk.Evaluate(row.LastTarget, lastActual, elapsed, forecast?.Last);
            result.Add(new FlightSla(row.FlightId, Assumption, onBlock, elapsed, allArrived, first, last));
        }

        return result;
    }
}
