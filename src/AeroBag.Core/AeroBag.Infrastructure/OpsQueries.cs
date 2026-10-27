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
    double? FirstBagMinutes,
    int Stalled,
    int ExitRead,
    int ExitOverdue,
    int ExitReview);

public sealed class OpsQueries(NpgsqlDataSource db)
{
    /// <summary>
    /// Bant ağzına gelmiş bir bagaj bu süreden uzun süredir alınmadıysa "takılı" sayılır.
    /// Simülatörde alınma gecikmesi ortalama ~140 sn olduğundan 20 dk eşiği sahte alarm üretmez.
    /// Takılı sayısı türetilmiş bir değerdir: bagajın durumu değiştirilmez.
    /// "Şimdi", BSM olmayan olayların en son zamanıdır (simülasyonda sanal saat, gerçekte gerçek saat olmalı).
    /// </summary>
    public const int StalledCarouselMinutes = 20;

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

        var stalled = new Dictionary<string, int>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT b.flight_id, count(*)::int
            FROM bags b
            JOIN LATERAL (
                SELECT max(e.occurred_at_utc) AS t
                FROM baggage_events e
                WHERE e.bag_id = b.bag_id AND e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'
            ) c ON TRUE
            WHERE b.current_state = 'ON_CAROUSEL'
              AND c.t < (SELECT max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM')
                        - make_interval(mins => @mins)
            GROUP BY b.flight_id
            """))
        {
            cmd.Parameters.AddWithValue("mins", StalledCarouselMinutes);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                stalled[r.GetString(0)] = r.GetInt32(1);
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
                   (count(*) FILTER (WHERE current_state IN ('CLAIMED_FROM_BELT','EXIT_OVERDUE','EXIT_CLEARED','EXIT_REVIEW')))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_CLEARED'))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_OVERDUE'))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_REVIEW'))::int
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
                    t.Anomalies, t.FirstUnload, t.FirstCarousel, t.LastCarousel, firstBagMinutes,
                    stalled.GetValueOrDefault(id), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)));
            }
        }

        return result;
    }

    private static DateTimeOffset? Ts(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}
