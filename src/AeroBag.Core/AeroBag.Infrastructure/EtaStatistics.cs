using AeroBag.Application.Eta;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

/// <summary>Olay günlüğünden iki durum arasındaki sürenin kantillerini hesaplar.</summary>
public sealed class EtaStatistics(NpgsqlDataSource db)
{
    public Task<DurationQuantiles> UnloadToCarouselAsync(CancellationToken ct = default) =>
        QuantilesAsync("UNLOADED_AT_ARRIVAL", "ON_CAROUSEL", ct);

    public Task<DurationQuantiles> ChuteToCarouselAsync(CancellationToken ct = default) =>
        QuantilesAsync("INBOUND_CHUTE", "ON_CAROUSEL", ct);

    private async Task<DurationQuantiles> QuantilesAsync(string fromState, string toState, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT count(*)::int,
                   percentile_cont(0.1) WITHIN GROUP (ORDER BY secs),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY secs),
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY secs)
            FROM (
                SELECT extract(epoch FROM (b.occurred_at_utc - a.occurred_at_utc))::double precision AS secs
                FROM baggage_events a
                JOIN baggage_events b ON b.bag_id = a.bag_id
                WHERE a.to_state = @from AND a.outcome <> 'IGNORED_STALE'
                  AND b.to_state = @to   AND b.outcome <> 'IGNORED_STALE'
                  AND b.occurred_at_utc > a.occurred_at_utc
            ) s
            """);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = fromState });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = toState });

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var n = r.GetInt32(0);
        return n == 0
            ? new DurationQuantiles(0, 0, 0, 0)
            : new DurationQuantiles(n, r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
    }
}
