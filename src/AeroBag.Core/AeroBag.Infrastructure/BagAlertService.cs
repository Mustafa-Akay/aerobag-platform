using System.Text.Json;
using AeroBag.Application.Alerts;
using AeroBag.Application.Time;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record AlertScanResult(int Opened, int Closed);

/// <summary>AODB kilometre taşlarını yazar (POST /v1/flights/milestones). Verilmeyen alan kayıtlı değeri korur.</summary>
public sealed class FlightMilestoneStore(NpgsqlDataSource db)
{
    public async Task UpsertAsync(FlightMilestones m, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO flight_milestones (flight_id, departed_utc, unloading_completed_utc, first_bag_on_belt_utc, last_bag_on_belt_utc,
                                           scheduled_departure_utc, updated_at_utc)
            VALUES (@id, @d, @u, @fb, @lb, @sd, now())
            ON CONFLICT (flight_id) DO UPDATE SET
              departed_utc = COALESCE(EXCLUDED.departed_utc, flight_milestones.departed_utc),
              unloading_completed_utc = COALESCE(EXCLUDED.unloading_completed_utc, flight_milestones.unloading_completed_utc),
              first_bag_on_belt_utc = COALESCE(EXCLUDED.first_bag_on_belt_utc, flight_milestones.first_bag_on_belt_utc),
              last_bag_on_belt_utc = COALESCE(EXCLUDED.last_bag_on_belt_utc, flight_milestones.last_bag_on_belt_utc),
              -- Planlanan kalkış değişebilir (tarife değişikliği, gecikme ilanı): son bildirilen geçerlidir.
              scheduled_departure_utc = COALESCE(EXCLUDED.scheduled_departure_utc, flight_milestones.scheduled_departure_utc),
              updated_at_utc = now()
            """);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Varchar) { Value = m.FlightId });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.TimestampTz) { Value = (object?)m.DepartedUtc?.UtcDateTime ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.TimestampTz) { Value = (object?)m.UnloadingCompletedUtc?.UtcDateTime ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("fb", NpgsqlDbType.TimestampTz) { Value = (object?)m.FirstBagOnBeltUtc?.UtcDateTime ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("lb", NpgsqlDbType.TimestampTz) { Value = (object?)m.LastBagOnBeltUtc?.UtcDateTime ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sd", NpgsqlDbType.TimestampTz) { Value = (object?)m.ScheduledDepartureUtc?.UtcDateTime ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Kayıp bavul uyarılarını açar ve kapatır (Faz 6, W4). Kurallar <see cref="BagAlertRules"/>'tadır; bu sınıf yalnızca
/// adayları okur, kuralı uygular ve farkı yazar. Her açılış ve kapanış için içeriksiz bir canlı bildirim (outbox) yazılır.
/// Aynı anda tek örnek çalışır (işlem kapsamlı danışma kilidi); ikinci örnek o turu atlar.
/// </summary>
public sealed class BagAlertService(NpgsqlDataSource db, AlertPolicy policy, IOperationalClock? operationalClock = null)
{
    private const long LockKey = 0x4165526F416C7274; // "AeRoAlrt"

    private readonly IOperationalClock _now = operationalClock ?? new EventDerivedClock(db);

    private sealed record Candidate(Guid BagId, string Tag, string FlightId, string State, DateTimeOffset? Departed, DateTimeOffset? UnloadingCompleted);

    public async Task<AlertScanResult> ScanAsync(CancellationToken ct = default)
    {
        var now = await _now.NowAsync(ct);
        return now is null ? new AlertScanResult(0, 0) : await ScanAsync(now.Value, ct);
    }

    public async Task<AlertScanResult> ScanAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lk = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@k)", conn, tx))
        {
            lk.Parameters.AddWithValue("k", LockKey);
            if (await lk.ExecuteScalarAsync(ct) is not true)
            {
                return new AlertScanResult(0, 0);
            }
        }

        // Adaylar: kilometre taşı toleransı geçmiş uçuşlarda varış öncesindeki bavullar, ve açık uyarısı olan her bavul.
        var candidates = new Dictionary<Guid, Candidate>();
        await using (var sel = new NpgsqlCommand(
            """
            SELECT b.bag_id, b.bag_tag_number, b.flight_id, b.current_state, m.departed_utc, m.unloading_completed_utc
            FROM bags b
            JOIN flight_milestones m ON m.flight_id = b.flight_id
            WHERE b.current_state IN ('CHECKED_IN', 'SECURITY_SCREENED', 'LOADED_ON_AIRCRAFT')
              AND b.inbound_flight_id IS NULL -- bağlantı satırı (W10a): kaçırılan bağlantı ayrı bir kuraldır (W10b)
              AND (m.departed_utc <= @nl OR m.unloading_completed_utc <= @mi)
            UNION
            SELECT b.bag_id, b.bag_tag_number, b.flight_id, b.current_state, m.departed_utc, m.unloading_completed_utc
            FROM bag_alerts a
            JOIN bags b ON b.bag_id = a.bag_id
            LEFT JOIN flight_milestones m ON m.flight_id = b.flight_id
            WHERE a.closed_at_utc IS NULL
            """, conn, tx))
        {
            sel.Parameters.Add(new NpgsqlParameter("nl", NpgsqlDbType.TimestampTz) { Value = (now - policy.NotLoadedGrace).UtcDateTime });
            sel.Parameters.Add(new NpgsqlParameter("mi", NpgsqlDbType.TimestampTz) { Value = (now - policy.MissingGrace).UtcDateTime });
            await using var r = await sel.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var c = new Candidate(r.GetGuid(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
                candidates[c.BagId] = c;
            }
        }

        var open = new Dictionary<Guid, List<(Guid AlertId, string Kind)>>();
        await using (var sel = new NpgsqlCommand("SELECT alert_id, bag_id, kind FROM bag_alerts WHERE closed_at_utc IS NULL", conn, tx))
        {
            await using var r = await sel.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var bagId = r.GetGuid(1);
                if (!open.TryGetValue(bagId, out var list))
                {
                    open[bagId] = list = [];
                }
                list.Add((r.GetGuid(0), r.GetString(2)));
            }
        }

        var opened = 0;
        var closed = 0;
        foreach (var c in candidates.Values)
        {
            var state = StateNames.FromDb(c.State)!.Value;
            var wanted = BagAlertRules.Evaluate(state, c.Departed, c.UnloadingCompleted, now, policy);
            var existing = open.GetValueOrDefault(c.BagId) ?? new List<(Guid AlertId, string Kind)>();
            var changed = false;

            foreach (var (alertId, kind) in existing.Where(e => e.Kind != wanted))
            {
                await using var up = new NpgsqlCommand(
                    "UPDATE bag_alerts SET closed_at_utc = @now, close_reason = @why WHERE alert_id = @id AND closed_at_utc IS NULL", conn, tx);
                up.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                up.Parameters.Add(new NpgsqlParameter("why", NpgsqlDbType.Varchar) { Value = BagAlertRules.CloseReason(kind, state) });
                up.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = alertId });
                closed += await up.ExecuteNonQueryAsync(ct);
                changed = true;
            }

            if (wanted is not null && existing.All(e => e.Kind != wanted))
            {
                await using var ins = new NpgsqlCommand(
                    """
                    INSERT INTO bag_alerts (alert_id, bag_id, kind, opened_at_utc) VALUES (@id, @bag, @kind, @now)
                    ON CONFLICT (bag_id, kind) WHERE closed_at_utc IS NULL DO NOTHING
                    """, conn, tx);
                ins.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
                ins.Parameters.Add(new NpgsqlParameter("bag", NpgsqlDbType.Uuid) { Value = c.BagId });
                ins.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Varchar) { Value = wanted });
                ins.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
                opened += await ins.ExecuteNonQueryAsync(ct);
                changed = true;
            }

            if (changed)
            {
                // İçeriksiz bildirim (bkz. LiveHub): yolcu ve operasyon ekranı güncel durumu mevcut uçlardan çeker.
                await using var ob = new NpgsqlCommand(
                    "INSERT INTO outbox_messages (id, topic, partition_key, payload) VALUES (@id, 'bag.alerts', @pk, @pl)", conn, tx);
                ob.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
                ob.Parameters.Add(new NpgsqlParameter("pk", NpgsqlDbType.Varchar) { Value = c.Tag });
                ob.Parameters.Add(new NpgsqlParameter("pl", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(new { bagTag = c.Tag, flightId = c.FlightId, alert = wanted }),
                });
                await ob.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        return new AlertScanResult(opened, closed);
    }
}

public sealed record OpenAlertView(string BagTag, string FlightId, string Kind, string State, DateTimeOffset OpenedAtUtc);

/// <summary>Operasyon panosu için açık uyarılar (en eskisi önce). Kişisel veri içermez.</summary>
public sealed class BagAlertQueries(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<OpenAlertView>> OpenAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT b.bag_tag_number, b.flight_id, a.kind, b.current_state, a.opened_at_utc
            FROM bag_alerts a
            JOIN bags b ON b.bag_id = a.bag_id
            WHERE a.closed_at_utc IS NULL
            ORDER BY a.opened_at_utc, b.bag_tag_number
            LIMIT @n
            """);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = limit });
        var list = new List<OpenAlertView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new OpenAlertView(r.GetString(0).Trim(), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
        }
        return list;
    }
}
