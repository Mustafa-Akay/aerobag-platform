using Npgsql;

namespace AeroBag.Infrastructure;

/// <summary>outbox_messages tablosundan okunan, henüz dağıtılmamış bir mesaj.</summary>
public sealed record OutboxMessage(Guid Id, string Topic, string PartitionKey, string PayloadJson);

/// <summary>Outbox mesajlarını dış dünyaya (SignalR, kuyruk vb.) ileten taraf.</summary>
public interface IOutboxPublisher
{
    /// <summary>Başarısızlıkta istisna fırlatır; mesaj sonra yeniden denenir.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

public sealed record DispatchResult(int Published, int Failed);

/// <summary>
/// Outbox satırlarını sırayla (seq) okuyup yayıncıya verir, başarılıları işlenmiş işaretler.
/// Garanti: en az bir kez teslim. Yayın başarılı olup işaretleme öncesi süreç çökerse mesaj tekrar gider;
/// alıcılar tekrarı zararsız karşılamalıdır (SignalR bildirimleri içeriksiz olduğu için öyledir).
/// FOR UPDATE SKIP LOCKED sayesinde birden çok örnek aynı satırı iki kez almaz; ancak çoklu örnekte
/// sıra garantisi azalır (şimdilik tek örnek varsayılır).
/// </summary>
public sealed class OutboxRelay(NpgsqlDataSource db, TimeProvider clock)
{
    /// <summary>Bu kadar başarısız denemeden sonra mesaj bekletilir (zehirli mesaj sırayı tıkamasın diye).</summary>
    public const int MaxAttempts = 5;

    public async Task<DispatchResult> DispatchOnceAsync(IOutboxPublisher publisher, int batchSize = 100, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var batch = new List<OutboxMessage>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, topic, partition_key, payload::text
            FROM outbox_messages
            WHERE processed_at_utc IS NULL AND attempts < @max
            ORDER BY seq
            LIMIT @n
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            select.Parameters.AddWithValue("max", MaxAttempts);
            select.Parameters.AddWithValue("n", batchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                batch.Add(new OutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var published = 0;
        var failed = 0;
        foreach (var message in batch)
        {
            bool ok;
            try
            {
                await publisher.PublishAsync(message, ct);
                ok = true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                ok = false;
            }

            await using var update = new NpgsqlCommand(
                ok
                    ? "UPDATE outbox_messages SET processed_at_utc = @now, attempts = attempts + 1 WHERE id = @id"
                    : "UPDATE outbox_messages SET attempts = attempts + 1 WHERE id = @id",
                conn, tx);
            update.Parameters.AddWithValue("id", message.Id);
            if (ok)
            {
                update.Parameters.AddWithValue("now", clock.GetUtcNow());
                published++;
            }
            else
            {
                failed++;
            }
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new DispatchResult(published, failed);
    }
}
