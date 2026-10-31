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
/// Okuma kapsamı (W5): bant ağzı okuması yoksa gerçekleşen ilk/son bavul, yer hizmetinin AODB'ye bildirdiği uçuş düzeyi
/// zamanlardan (flight_milestones) gelir. Kaynak <see cref="FlightSla.TimesSource"/> alanında yazar.
/// </summary>
/// <param name="TimesSource">
/// Gerçekleşen zamanların kaynağı: READS (bant ağzı okumaları), AODB (uçuş düzeyi bildirim) ya da NONE (henüz yok).
/// </param>
public sealed record FlightSla(
    string FlightId,
    string Assumption,
    DateTimeOffset? OnBlockUtc,
    double? ElapsedSec,
    bool AllBagsArrived,
    SlaLeg FirstBag,
    SlaLeg LastBag,
    string TimesSource);

public sealed class SlaQueries(NpgsqlDataSource db, IMlPredictor ml, IOperationalClock? clock = null)
{
    private readonly IOperationalClock _now = clock ?? new EventDerivedClock(db);

    public const string Assumption = "DEMO_ASSUMPTION";

    private sealed record Row(string FlightId, int FirstTarget, int LastTarget, int Total, int NotArrived,
        DateTimeOffset? FirstCarousel, DateTimeOffset? LastCarousel, DateTimeOffset? AodbFirst, DateTimeOffset? AodbLast);

    public const string SourceReads = "READS";
    public const string SourceAodb = "AODB";
    public const string SourceNone = "NONE";

    public async Task<IReadOnlyList<FlightSla>> GetAsync(CancellationToken ct = default)
    {
        var now = await _now.NowAsync(ct);

        var rows = new List<Row>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT f.flight_id, f.sla_first_bag_sec, f.sla_last_bag_sec,
                   (SELECT count(*)::int FROM bags b WHERE b.flight_id = f.flight_id AND b.onward_flight_id IS NULL),
                   (SELECT count(*)::int FROM bags b WHERE b.flight_id = f.flight_id AND b.onward_flight_id IS NULL
                      AND b.current_state IN ('CHECKED_IN','SECURITY_SCREENED','LOADED_ON_AIRCRAFT','UNLOADED_AT_ARRIVAL','INBOUND_CHUTE')),
                   x.first_t, x.last_t,
                   m.first_bag_on_belt_utc, m.last_bag_on_belt_utc
            FROM flights f
            LEFT JOIN flight_milestones m ON m.flight_id = f.flight_id
            LEFT JOIN LATERAL (
                SELECT min(c.t) AS first_t, max(c.t) AS last_t
                FROM (
                    SELECT min(e.occurred_at_utc) AS t
                    FROM bags b JOIN baggage_events e ON e.bag_id = b.bag_id
                    WHERE b.flight_id = f.flight_id AND e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'
                    GROUP BY b.bag_id
                ) c
            ) x ON TRUE
            -- W10a: aktarma bavulları banda gelmez (yukarıdaki sayımlara girmez); yalnızca bağlantı satırı taşıyan uçuşlar
            -- buradan çıkış yapan uçuşlardır, varış SLA'sı yoktur.
            WHERE NOT EXISTS (SELECT 1 FROM bags t WHERE t.flight_id = f.flight_id AND t.inbound_flight_id IS NOT NULL)
            ORDER BY f.flight_id
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                    r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                    r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
                    r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8)));
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
                result.Add(new FlightSla(row.FlightId, Assumption, onBlock, null, false, empty, empty with { TargetSec = row.LastTarget }, SourceNone));
                continue;
            }

            var elapsed = Math.Max((now.Value - onBlock.Value).TotalSeconds, 0);
            // Bant okumaları varsa onlar, yoksa AODB'nin uçuş düzeyi zamanları. Son bavul: okumalarda tüm bavullar banda
            // gelmiş olmalı; AODB "son bavul bantta" dediyse o geçerlidir (kayıp bavul bunu bekletmez).
            var readsAllArrived = row.Total > 0 && row.NotArrived == 0 && row.LastCarousel is not null;
            var firstTime = row.FirstCarousel ?? row.AodbFirst;
            var lastTime = readsAllArrived ? row.LastCarousel : row.AodbLast;
            var allArrived = readsAllArrived || row.AodbLast is not null;
            var source = row.FirstCarousel is not null || readsAllArrived ? SourceReads
                : firstTime is not null || lastTime is not null ? SourceAodb
                : SourceNone;
            double? firstActual = firstTime is { } f ? (f - onBlock.Value).TotalSeconds : null;
            double? lastActual = lastTime is { } l ? (l - onBlock.Value).TotalSeconds : null;
            var forecast = firstActual is null || lastActual is null ? await ml.PredictAsync(ctx!, ct) : null;

            var first = SlaRisk.Evaluate(row.FirstTarget, firstActual, elapsed, forecast?.First);
            var last = SlaRisk.Evaluate(row.LastTarget, lastActual, elapsed, forecast?.Last);
            result.Add(new FlightSla(row.FlightId, Assumption, onBlock, elapsed, allArrived, first, last, source));
        }

        return result;
    }
}
