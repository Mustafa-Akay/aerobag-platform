using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record RetentionOptions(int PersonalDataDays);

/// <summary>
/// Saklama süresi (KVKK notu): bavullarının hepsi teslim edilmiş (banttan alınmış ya da çıkış durumunda) ve en son
/// güncellemesinin üzerinden <see cref="RetentionOptions.PersonalDataDays"/> gün geçmiş yolcuların arama özetleri
/// (lookup_key, pnr_key) rastgele değerle değiştirilir. Gizli anahtarı bilen biri bile artık PNR ve soyaddan bu yolcuya
/// ulaşamaz; bavul ve olay kayıtları (kişisel veri içermeyen) operasyon istatistiği için kalır.
/// Uygulama rolü yalnızca bu sütunları güncelleyebilir; silme yetkisi yoktur.
/// </summary>
public sealed class RetentionService(NpgsqlDataSource db, TimeProvider clock, RetentionOptions options)
{
    private static readonly string[] Delivered = ["CLAIMED_FROM_BELT", "EXIT_OVERDUE", "EXIT_CLEARED", "EXIT_REVIEW", "OFFLOADED_NO_SHOW"];

    public async Task<int> AnonymizeDueAsync(int batchSize = 500, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var cmd = db.CreateCommand(
            """
            WITH due AS (
                SELECT p.passenger_id
                FROM passengers p
                JOIN bags b ON b.passenger_id = p.passenger_id
                WHERE p.anonymized_at_utc IS NULL
                GROUP BY p.passenger_id
                HAVING bool_and(b.current_state = ANY(@delivered))
                   AND max(b.updated_at_utc) < @cutoff
                LIMIT @n
            )
            UPDATE passengers p
            SET lookup_key = sha256(convert_to(p.passenger_id::text || random()::text || clock_timestamp()::text, 'UTF8')),
                pnr_key    = sha256(convert_to(random()::text || p.passenger_id::text || clock_timestamp()::text, 'UTF8')),
                anonymized_at_utc = @now
            FROM due
            WHERE p.passenger_id = due.passenger_id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("delivered", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = Delivered });
        cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = (now - TimeSpan.FromDays(options.PersonalDataDays)).UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = batchSize });
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
