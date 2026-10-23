using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record BagView(string BagTag, string FlightId, string State, int Version, DateTimeOffset UpdatedAtUtc);

public sealed class BagQueries(NpgsqlDataSource db)
{
    public async Task<BagView?> GetAsync(string bagTag, string? flightId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT bag_tag_number, flight_id, current_state, state_version, updated_at_utc
            FROM bags
            WHERE bag_tag_number = @t AND (@f::text IS NULL OR flight_id = @f)
            ORDER BY updated_at_utc DESC
            LIMIT 1
            """);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = bagTag });
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Text) { Value = (object?)flightId ?? DBNull.Value });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new BagView(
            reader.GetString(0).Trim(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }
}
