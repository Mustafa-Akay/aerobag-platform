using AeroBag.Application.Coverage;
using AeroBag.Application.Time;
using Npgsql;
using NpgsqlTypes;

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
    int? Stalled,
    int ExitRead,
    int ExitOverdue,
    int ExitReview,
    string? Carousel,
    int OpenAlerts,
    int Transfer);

/// <remarks>
/// Okuma kapsamı (W5): bant ağzı okuması yoksa ilk/son bavul zamanı AODB'nin uçuş düzeyi bildiriminden gelir ve
/// "takılı" bavul sayılamaz (null: bilinmiyor). "Bantta" sayacı bu kurulumda okumaya dayanmadığı için sıfır kalır.
/// <para>
/// Aktarma (W10a): pano gelen uçuşları gösterir. Bağlantı uçuşundaki satırlar (inbound_flight_id dolu) burada sayılmaz;
/// onlar <see cref="TransferQueries"/>'tedir. Gelen uçuştaki aktarma bavulları <c>Transfer</c>'da sayılır ve banda gelmeyecekleri
/// için varış sayaçlarına (varış öncesi, yolda, bantta, alındı) girmez; toplam ikisinin toplamıdır.
/// </para>
/// </remarks>
public sealed class OpsQueries(NpgsqlDataSource db, IOperationalClock? clock = null, ReadCoverage? coverage = null)
{
    private readonly IOperationalClock _now = clock ?? new EventDerivedClock(db);
    private readonly ReadCoverage _coverage = coverage ?? ReadCoverage.Full;

    /// <summary>
    /// Bant ağzına gelmiş bir bagaj bu süreden uzun süredir alınmadıysa "takılı" sayılır.
    /// Simülatörde alınma gecikmesi ortalama ~140 sn olduğundan 20 dk eşiği sahte alarm üretmez.
    /// Takılı sayısı türetilmiş bir değerdir: bagajın durumu değiştirilmez.
    /// "Şimdi" operasyon saatinden gelir (IOperationalClock); saat yoksa (henüz olay yok) takılı bavul sayılmaz.
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
        var now = await _now.NowAsync(ct);
        if (now is not null && _coverage.Has(ReadPoint.Carousel))
        {
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
                  AND c.t < @now - make_interval(mins => @mins)
                GROUP BY b.flight_id
                """))
            {
                cmd.Parameters.AddWithValue("mins", StalledCarouselMinutes);
                cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.Value.UtcDateTime });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    stalled[r.GetString(0)] = r.GetInt32(1);
                }
            }
        }

        var result = new List<FlightSummary>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT flight_id,
                   count(*)::int,
                   (count(*) FILTER (WHERE onward_flight_id IS NULL AND current_state IN ('CHECKED_IN','SECURITY_SCREENED','LOADED_ON_AIRCRAFT')))::int,
                   (count(*) FILTER (WHERE onward_flight_id IS NULL AND current_state IN ('UNLOADED_AT_ARRIVAL','INBOUND_CHUTE')))::int,
                   (count(*) FILTER (WHERE onward_flight_id IS NULL AND current_state = 'ON_CAROUSEL'))::int,
                   (count(*) FILTER (WHERE onward_flight_id IS NULL AND current_state IN ('CLAIMED_FROM_BELT','EXIT_OVERDUE','EXIT_CLEARED','EXIT_REVIEW')))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_CLEARED'))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_OVERDUE'))::int,
                   (count(*) FILTER (WHERE current_state = 'EXIT_REVIEW'))::int,
                   (SELECT fc.carousel FROM flight_context fc WHERE fc.flight_id = bags.flight_id),
                   (SELECT count(*) FROM bag_alerts a JOIN bags x ON x.bag_id = a.bag_id
                     WHERE x.flight_id = bags.flight_id AND a.closed_at_utc IS NULL)::int,
                   (SELECT m.first_bag_on_belt_utc FROM flight_milestones m WHERE m.flight_id = bags.flight_id),
                   (SELECT m.last_bag_on_belt_utc FROM flight_milestones m WHERE m.flight_id = bags.flight_id),
                   (count(*) FILTER (WHERE onward_flight_id IS NOT NULL))::int
            FROM bags
            WHERE inbound_flight_id IS NULL
            GROUP BY flight_id
            ORDER BY flight_id
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                times.TryGetValue(id, out var t);
                // Bant okuması yoksa AODB'nin uçuş düzeyi zamanları.
                var firstBelt = t.FirstCarousel ?? Ts(r, 11);
                var lastBelt = t.LastCarousel ?? Ts(r, 12);
                double? firstBagMinutes = t.FirstUnload is { } u && firstBelt is { } c
                    ? Math.Round((c - u).TotalMinutes, 1)
                    : null;
                int? stalledCount = _coverage.Has(ReadPoint.Carousel) ? stalled.GetValueOrDefault(id) : null;
                result.Add(new FlightSummary(id, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    t.Anomalies, t.FirstUnload, firstBelt, lastBelt, firstBagMinutes,
                    stalledCount, r.GetInt32(6), r.GetInt32(7), r.GetInt32(8),
                    r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10), r.GetInt32(13)));
            }
        }

        return result;
    }

    private static DateTimeOffset? Ts(NpgsqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}
