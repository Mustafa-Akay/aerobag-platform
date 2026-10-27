using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

/// <summary>
/// Banttan alındıktan sonra tanımlı sürede çıkış kapısında okunmayan bavulları "çıkış okunmadı" (EXIT_OVERDUE) yapar.
/// Bu bir sonuç değil, inceleme gerektiren durumdur: etiket sorunu, salonda unutulma ya da okuma kaçırması olabilir.
/// Yolcuya bildirim gitmez. Süre dolduktan sonra çıkış okunursa bavul EXIT_CLEARED olur (EXIT_OVERDUE son durum değildir).
/// </summary>
public sealed class ExitDeadlineService(NpgsqlDataSource db, TimeProvider clock)
{
    /// <summary>
    /// "Şimdi": BSM olmayan olayların en son zamanı (simülasyonda sanal saat; yolcu ETA'sı ve takılı bavul sayacıyla aynı kabul).
    /// Gerçek kurulumda gerçek saat kullanılmalıdır. Olay akışı durursa bu saat de durur; süre dolumları o zamana kadar işlenmez.
    /// </summary>
    public async Task<DateTimeOffset?> VirtualNowAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM'");
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
    }

    public async Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = await VirtualNowAsync(ct);
        return now is null ? 0 : await ProcessDueAsync(now.Value, batchSize, ct);
    }

    /// <summary>Süresi <paramref name="now"/> anına kadar dolan bekleyen kayıtları işler; EXIT_OVERDUE yapılan bavul sayısını döndürür.</summary>
    public async Task<int> ProcessDueAsync(DateTimeOffset now, int batchSize, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var due = new List<(Guid BagId, DateTimeOffset DueAt, string FlightId, string Tag, string State)>();
        await using (var sel = new NpgsqlCommand(
            """
            SELECT d.bag_id, d.due_at_utc, b.flight_id, b.bag_tag_number, b.current_state
            FROM exit_deadlines d
            JOIN bags b ON b.bag_id = d.bag_id
            WHERE d.status = 'PENDING' AND d.due_at_utc <= @now
            ORDER BY d.due_at_utc
            LIMIT @n
            FOR UPDATE OF d SKIP LOCKED
            """, conn, tx))
        {
            sel.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            sel.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = batchSize });
            await using var r = await sel.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                due.Add((r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3).Trim(), r.GetString(4)));
            }
        }

        var overdue = 0;
        var received = clock.GetUtcNow().UtcDateTime;
        foreach (var d in due)
        {
            // Bavul bu arada başka bir duruma geçtiyse (okuma geç işlendi) yalnızca kaydı kapat.
            if (d.State != "CLAIMED_FROM_BELT")
            {
                await ExecAsync(conn, tx, "UPDATE exit_deadlines SET status = 'CLEARED' WHERE bag_id = @id",
                    new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = d.BagId });
                continue;
            }

            await ExecAsync(conn, tx,
                "UPDATE bags SET current_state = 'EXIT_OVERDUE', state_version = state_version + 1, updated_at_utc = @now WHERE bag_id = @id",
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = received },
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = d.BagId });

            // Tekil anahtar: aynı bavul için bu olay iki kez yazılamaz.
            await ExecAsync(conn, tx,
                """
                INSERT INTO baggage_events (event_id, idempotency_key, bag_id, message_type, from_state, to_state, outcome,
                                            scanner_id, source, raw_text, payload, occurred_at_utc, received_at_utc)
                VALUES (@eid, @ik, @bid, 'SYSTEM', 'CLAIMED_FROM_BELT', 'EXIT_OVERDUE', 'APPLIED',
                        'exit-worker', 'system', NULL, @pl, @occ, @rec)
                """,
                new NpgsqlParameter("eid", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
                new NpgsqlParameter("ik", NpgsqlDbType.Varchar) { Value = $"exit-overdue:{d.BagId}" },
                new NpgsqlParameter("bid", NpgsqlDbType.Uuid) { Value = d.BagId },
                new NpgsqlParameter("pl", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { reason = "EXIT_DEADLINE_PASSED" }) },
                new NpgsqlParameter("occ", NpgsqlDbType.TimestampTz) { Value = d.DueAt.UtcDateTime },
                new NpgsqlParameter("rec", NpgsqlDbType.TimestampTz) { Value = received });

            await ExecAsync(conn, tx,
                "INSERT INTO outbox_messages (id, topic, partition_key, payload, created_at_utc) VALUES (@id, 'baggage.events', @pk, @pl, @now)",
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() },
                new NpgsqlParameter("pk", NpgsqlDbType.Varchar) { Value = d.Tag },
                new NpgsqlParameter("pl", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new
                {
                    bagTag = d.Tag, flightId = d.FlightId, fromState = "CLAIMED_FROM_BELT", toState = "EXIT_OVERDUE",
                    outcome = "APPLIED", occurredAtUtc = d.DueAt,
                }) },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = received });

            await ExecAsync(conn, tx, "UPDATE exit_deadlines SET status = 'OVERDUE' WHERE bag_id = @id",
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = d.BagId });
            overdue++;
        }

        await tx.CommitAsync(ct);
        return overdue;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        await cmd.ExecuteNonQueryAsync();
    }
}
