using AeroBag.Application.Ml;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record PendingOutcome(FlightContext Context, double FirstMin, double LastMin);

public sealed class FlightContextStore(NpgsqlDataSource db)
{
    public async Task UpsertAsync(FlightContext c, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO flight_context (flight_id, on_block_utc, wide, rain, bags, stand_m, chute_m, speed_mps, arrivals_30, handler)
            VALUES (@id, @ob, @w, @r, @b, @sm, @cm, @sp, @a, @h)
            ON CONFLICT (flight_id) DO UPDATE SET
              on_block_utc = EXCLUDED.on_block_utc, wide = EXCLUDED.wide, rain = EXCLUDED.rain, bags = EXCLUDED.bags,
              stand_m = EXCLUDED.stand_m, chute_m = EXCLUDED.chute_m, speed_mps = EXCLUDED.speed_mps,
              arrivals_30 = EXCLUDED.arrivals_30, handler = EXCLUDED.handler
            """);
        AddCommon(cmd, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, FlightContext>> GetAsync(IReadOnlyCollection<string> flightIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, FlightContext>();
        if (flightIds.Count == 0) return result;

        await using var cmd = db.CreateCommand(
            """
            SELECT flight_id, on_block_utc, wide, rain, bags, stand_m, chute_m, speed_mps, arrivals_30, handler
            FROM flight_context WHERE flight_id = ANY(@ids)
            """);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = flightIds.ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var c = new FlightContext(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.GetBoolean(2), r.GetBoolean(3),
                r.GetInt32(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt32(8), r.GetString(9));
            result[c.FlightId] = c;
        }
        return result;
    }

    /// <summary>
    /// Bitmiş sayılan uçuşlar: bavulların en az %90'ı banda gelmiş ve en son banda geliş olayından sonra
    /// (<paramref name="now"/>, operasyon saati) en az 10 dakika geçmiş. İlk/son bavul, veritabanında görülen bavullardan hesaplanır;
    /// kayıp mesajlar yüzünden son bavul bir miktar erken görünebilir.
    /// </summary>
    public async Task<List<PendingOutcome>> PendingOutcomesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT c.flight_id, c.on_block_utc, c.wide, c.rain, c.bags, c.stand_m, c.chute_m, c.speed_mps, c.arrivals_30, c.handler,
                   min(x.t), max(x.t)
            FROM flight_context c
            JOIN bags b ON b.flight_id = c.flight_id
            JOIN LATERAL (
                SELECT min(e.occurred_at_utc) AS t FROM baggage_events e
                WHERE e.bag_id = b.bag_id AND e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'
            ) x ON x.t IS NOT NULL
            WHERE NOT c.outcome_reported
            GROUP BY c.flight_id, c.on_block_utc, c.wide, c.rain, c.bags, c.stand_m, c.chute_m, c.speed_mps, c.arrivals_30, c.handler
            HAVING count(*) >= c.bags * 0.9
               AND @now >= max(x.t) + interval '10 minutes'
            """);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        var list = new List<PendingOutcome>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var onBlock = r.GetFieldValue<DateTimeOffset>(1);
            var c = new FlightContext(r.GetString(0), onBlock, r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4),
                r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt32(8), r.GetString(9));
            var first = (r.GetFieldValue<DateTimeOffset>(10) - onBlock).TotalMinutes;
            var last = (r.GetFieldValue<DateTimeOffset>(11) - onBlock).TotalMinutes;
            if (first >= 0 && last >= first)
            {
                list.Add(new PendingOutcome(c, first, last));
            }
        }
        return list;
    }

    /// <summary>
    /// Banta gelmiş ve henüz ML servisine bildirilmemiş bavullar (en eski bant gelişinden başlayarak).
    /// Süre, olay günlüğündeki indirme ve bant olaylarından hesaplanır (eğitimdeki hedefle aynı tanım).
    /// </summary>
    public async Task<List<(Guid BagId, BagOutcome Outcome)>> PendingBagOutcomesAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT b.bag_id, c.flight_id, c.on_block_utc, c.wide, c.rain, c.bags, c.stand_m, c.chute_m, c.speed_mps, c.arrivals_30, c.handler,
                   extract(epoch FROM (car.t - unl.t))::double precision
            FROM bags b
            JOIN flight_context c ON c.flight_id = b.flight_id
            JOIN LATERAL (
                SELECT min(e.occurred_at_utc) AS t FROM baggage_events e
                WHERE e.bag_id = b.bag_id AND e.to_state = 'UNLOADED_AT_ARRIVAL' AND e.outcome <> 'IGNORED_STALE'
            ) unl ON unl.t IS NOT NULL
            JOIN LATERAL (
                SELECT min(e.occurred_at_utc) AS t FROM baggage_events e
                WHERE e.bag_id = b.bag_id AND e.to_state = 'ON_CAROUSEL' AND e.outcome <> 'IGNORED_STALE'
            ) car ON car.t IS NOT NULL
            WHERE car.t > unl.t
              AND NOT EXISTS (SELECT 1 FROM ml_bag_outcomes r WHERE r.bag_id = b.bag_id)
            ORDER BY car.t
            LIMIT @n
            """);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = limit });
        var list = new List<(Guid, BagOutcome)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var c = new FlightContext(r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(3), r.GetBoolean(4),
                r.GetInt32(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetInt32(9), r.GetString(10));
            list.Add((r.GetGuid(0), new BagOutcome(c, r.GetDouble(11))));
        }
        return list;
    }

    public async Task MarkBagOutcomesReportedAsync(IReadOnlyCollection<Guid> bagIds, CancellationToken ct = default)
    {
        if (bagIds.Count == 0) return;
        await using var cmd = db.CreateCommand(
            "INSERT INTO ml_bag_outcomes (bag_id) SELECT unnest(@ids) ON CONFLICT (bag_id) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = bagIds.ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkReportedAsync(string flightId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE flight_context SET outcome_reported = TRUE WHERE flight_id = @id");
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Varchar) { Value = flightId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddCommon(NpgsqlCommand cmd, FlightContext c)
    {
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Varchar) { Value = c.FlightId });
        cmd.Parameters.Add(new NpgsqlParameter("ob", NpgsqlDbType.TimestampTz) { Value = c.OnBlockUtc.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Boolean) { Value = c.Wide });
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Boolean) { Value = c.Rain });
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Integer) { Value = c.Bags });
        cmd.Parameters.Add(new NpgsqlParameter("sm", NpgsqlDbType.Double) { Value = c.StandM });
        cmd.Parameters.Add(new NpgsqlParameter("cm", NpgsqlDbType.Double) { Value = c.ChuteM });
        cmd.Parameters.Add(new NpgsqlParameter("sp", NpgsqlDbType.Double) { Value = c.SpeedMps });
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Integer) { Value = c.Arrivals30 });
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Varchar) { Value = c.Handler });
    }
}
