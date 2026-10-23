using Npgsql;

namespace AeroBag.Infrastructure;

public sealed record FlightSummary(
    string FlightId,
    int TotalBags,
    int BeforeArrival,
    int Inbound,
    int OnCarousel,
    int Claimed,
    int Anomalies,
    DateTimeOffset? FirstUnloadUtc,
    DateTimeOffset? FirstCarouselUtc,
    DateTimeOffset? LastCarouselUtc,
    double? FirstBagMinutes);

public sealed class OpsQueries(NpgsqlDataSource db)
{
    /// <summary>Uçuş bazında bavul sayaçları ve ilk/son bavul zamanları (operasyon panosu için).</summary>
    public async Task<IReadOnlyList<FlightSummary>> FlightsAsync(CancellationToken ct = default)
    {
        var times = new Dictionary<string, (int Anomalies, DateTimeOffset? FirstUnload, DateTimeOffset? FirstCarousel, DateTimeOffset? LastCarousel)>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT b.flight_id,
                   count(*) FILTER (WHERE e.outcome = 'ANOMALY')::int,
                   min(e.occurred_at_utc) FILTER (WHERE e.to_state = 'UNLOADED_AT_ARRIVAL' AND e.outcome <> 'IGNORED_STALE'),
                   min(e.occurred_at_utc) FILTER (WHERE e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'),
                   max(e.occurred_at_utc) FILTER (WHERE e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE')
            FROM baggage_events e
            JOIN bags b ON b.bag_id = e.bag_id
            GROUP BY b.flight_id
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                times[r.GetString(0)] = (r.GetInt32(1), Ts(r, 2), Ts(r, 3), Ts(r, 4));
            }
        }

        var result = new List<FlightSummary>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT flight_id,
                   count(*)::int,
                   (count(*) FILTER (WHERE current_state IN ('CHECKED_IN','SECURITY_SCREENED','LOADED_ON_AIRCRAFT')))::int,
                   (count(*) FILTER (WHERE current_state IN ('UNLOADED_AT_ARRIVAL','INBOUND_CHUTE')))::int,
                   (count(*) FILTER (WHERE current_state = 'ON_CAROUSEL'))::int,
                   (count(*) FILTER (WHERE current_state = 'CLAIMED_FROM_BELT'))::int
            FROM bags
            GROUP BY flight_id
            ORDER BY flight_id
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                times.TryGetValue(id, out var t);
                double? firstBagMinutes = t.FirstUnload is { } u && t.FirstCarousel is { } c
                    ? Math.Round((c - u).TotalMinutes, 1)
                    : null;
                result.Add(new FlightSummary(id, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    t.Anomalies, t.FirstUnload, t.FirstCarousel, t.LastCarousel, firstBagMinutes));
            }
        }

        return result;
    }

    private static DateTimeOffset? Ts(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}
