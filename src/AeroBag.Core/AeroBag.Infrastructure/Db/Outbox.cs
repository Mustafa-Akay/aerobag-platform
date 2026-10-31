using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure.Db;

/// <summary>Outbox konuları. <c>OutboxRelay</c> bunları canlı kanala dağıtır.</summary>
public static class OutboxTopics
{
    public const string BaggageEvents = "baggage.events";
    public const string FlightContext = "flight.context";
    public const string BagAlerts = "bag.alerts";
}

/// <summary>
/// <c>outbox_messages</c> tablosuna yazım. Çağıranın işlemi içinde çalışır; böylece mesaj, durumu değiştiren yazımla
/// birlikte kaydedilir ya da hiç kaydedilmez.
/// </summary>
public static class Outbox
{
    /// <summary>
    /// Mesajı yazar. <paramref name="createdAtUtc"/> null ise sütun yazılmaz ve veritabanı varsayılanı (<c>NOW()</c>) kullanılır;
    /// W12 öncesinde uyarı ve bant bildirimleri böyle yazılıyordu, davranış korunur.
    /// </summary>
    public static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string topic, string partitionKey,
        string payloadJson, DateTimeOffset? createdAtUtc, CancellationToken ct)
    {
        var sql = createdAtUtc is null
            ? "INSERT INTO outbox_messages (id, topic, partition_key, payload) VALUES (@id, @topic, @pk, @pl)"
            : "INSERT INTO outbox_messages (id, topic, partition_key, payload, created_at_utc) VALUES (@id, @topic, @pk, @pl, @now)";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter("topic", NpgsqlDbType.Varchar) { Value = topic });
        cmd.Parameters.Add(new NpgsqlParameter("pk", NpgsqlDbType.Varchar) { Value = partitionKey });
        cmd.Parameters.Add(new NpgsqlParameter("pl", NpgsqlDbType.Jsonb) { Value = payloadJson });
        if (createdAtUtc is { } created)
        {
            cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = created.UtcDateTime });
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
