using System.Text.Json;
using AeroBag.Application.TypeB;
using AeroBag.Domain;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

/// <summary>
/// Ham Type B mesajını alır; ayrıştırır, durum makinesinden geçirir ve tek bir veritabanı
/// işleminde olay günlüğü + bavul durumu + outbox (+ çıkış süresi) kayıtlarını yazar.
/// Ayrıştırılamayan mesaj ölü mektup tablosuna gider. Aynı bavul için işlemler
/// advisory lock ile sıraya dizilir; aynı idempotency_key ikinci kez işlenmez.
/// </summary>
public sealed class BaggageEventProcessor(NpgsqlDataSource db, TimeProvider clock, LookupKeyHasher hasher)
{
    public static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(180);

    public async Task<ProcessResult> ProcessAsync(string? rawText, string source, CancellationToken ct = default)
    {
        var received = clock.GetUtcNow();
        var parsed = TypeBParser.Parse(rawText, DateOnly.FromDateTime(received.UtcDateTime));
        if (!parsed.IsSuccess)
        {
            var error = $"{parsed.Error}: {parsed.Detail}";
            await DeadLetterAsync(source, rawText, error, ct);
            return ProcessResult.Reject(error);
        }

        if (!BaggageEventMapper.TryMap(parsed.Message!, received, out var ev, out var mapError))
        {
            await DeadLetterAsync(source, rawText, mapError ?? "Eşlenemedi.", ct);
            return ProcessResult.Reject(mapError ?? "Eşlenemedi.");
        }

        return await StoreAsync(ev!, rawText ?? string.Empty, received, ct);
    }

    private async Task<ProcessResult> StoreAsync(BaggageEvent ev, string rawText, DateTimeOffset received, CancellationToken ct)
    {
        var flightId = $"{ev.FlightNumber}-{ev.FlightDate:yyyyMMdd}";

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
                         P("k", $"{ev.BagTag}|{flightId}")))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var dupCmd = Cmd(conn, tx, "SELECT 1 FROM baggage_events WHERE idempotency_key = @k",
                         P("k", ev.IdempotencyKey)))
        {
            if (await dupCmd.ExecuteScalarAsync(ct) is not null)
            {
                return ProcessResult.Dup(ev.BagTag, flightId);
            }
        }

        // Uçuş bağlamı normalde AODB'den gelir; şimdilik bilinmeyen alanlar yer tutucudur.
        await using (var flightCmd = Cmd(conn, tx,
                         """
                         INSERT INTO flights (flight_id, airline_code, flight_number, flight_date,
                                              aircraft_type, origin_airport, destination_airport, is_international)
                         VALUES (@id, @al, @fn, @fd, 'UNK', 'XXX', 'XXX', FALSE)
                         ON CONFLICT (flight_id) DO NOTHING
                         """,
                         P("id", flightId), P("al", ev.FlightNumber[..2]), P("fn", ev.FlightNumber), P("fd", ev.FlightDate)))
        {
            await flightCmd.ExecuteNonQueryAsync(ct);
        }

        // Yolcu yalnızca HMAC arama anahtarıyla saklanır (ad, soyad ve PNR düz metin tutulmaz).
        Guid? passengerId = null;
        if (ev.Pnr is not null && ev.Surname is not null)
        {
            var lookupKey = hasher.Compute(ev.Pnr, ev.Surname);
            await using (var pc = Cmd(conn, tx,
                             """
                             INSERT INTO passengers (passenger_id, flight_id, lookup_key, cabin_class)
                             VALUES (@id, @f, @k, @c)
                             ON CONFLICT (flight_id, lookup_key) DO NOTHING
                             """,
                             P("id", Guid.NewGuid()), P("f", flightId), PB("k", lookupKey), P("c", ev.CabinClass ?? "Y")))
            {
                await pc.ExecuteNonQueryAsync(ct);
            }

            await using (var ps = Cmd(conn, tx,
                             "SELECT passenger_id FROM passengers WHERE flight_id = @f AND lookup_key = @k",
                             P("f", flightId), PB("k", lookupKey)))
            {
                passengerId = (Guid?)await ps.ExecuteScalarAsync(ct);
            }
        }

        var bagId = Guid.NewGuid();
        var bagExists = false;
        BagState? current = null;
        await using (var selCmd = Cmd(conn, tx,
                         "SELECT bag_id, current_state FROM bags WHERE bag_tag_number = @t AND flight_id = @f",
                         P("t", ev.BagTag), P("f", flightId)))
        {
            await using var reader = await selCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                bagExists = true;
                bagId = reader.GetGuid(0);
                current = StateNames.FromDb(reader.GetString(1));
            }
        }

        var transition = BagStateMachine.Apply(current, ev.TargetState);
        var stale = transition.Outcome == TransitionOutcome.IgnoredStale;
        var outcome = transition.Outcome switch
        {
            TransitionOutcome.Applied => "APPLIED",
            TransitionOutcome.AppliedWithAnomaly => "ANOMALY",
            _ => "IGNORED_STALE",
        };
        var newState = transition.NewState;
        var exitCleared = newState == BagState.ExitCleared;

        if (!bagExists)
        {
            await using var ins = Cmd(conn, tx,
                """
                INSERT INTO bags (bag_id, bag_tag_number, flight_id, passenger_id, current_state, state_version, exit_cleared, updated_at_utc)
                VALUES (@id, @t, @f, @p, @s, 1, @ec, @now)
                """,
                P("id", bagId), P("t", ev.BagTag), P("f", flightId), P("p", passengerId), P("s", StateNames.ToDb(newState)),
                P("ec", exitCleared), P("now", received));
            await ins.ExecuteNonQueryAsync(ct);
        }
        else if (!stale)
        {
            await using var upd = Cmd(conn, tx,
                """
                UPDATE bags SET current_state = @s, state_version = state_version + 1,
                                exit_cleared = @ec, updated_at_utc = @now
                WHERE bag_id = @id
                """,
                P("s", StateNames.ToDb(newState)), P("ec", exitCleared), P("now", received), P("id", bagId));
            await upd.ExecuteNonQueryAsync(ct);
        }

        if (bagExists && passengerId is not null)
        {
            // Bavul BSM'den önce başka bir olayla oluşmuş olabilir; yolcu sonradan bağlanır.
            await using var link = Cmd(conn, tx,
                "UPDATE bags SET passenger_id = @p WHERE bag_id = @id AND passenger_id IS NULL",
                P("p", passengerId), P("id", bagId));
            await link.ExecuteNonQueryAsync(ct);
        }

        var payload = JsonSerializer.Serialize(new
        {
            bagTag = ev.BagTag,
            sequence = ev.Sequence,
            flightId,
            scanPoint = ev.ScanPoint,
            targetState = StateNames.ToDb(ev.TargetState),
            skippedStates = transition.SkippedStates.Select(StateNames.ToDb).ToArray(),
        });

        await using (var evCmd = Cmd(conn, tx,
                         """
                         INSERT INTO baggage_events (event_id, idempotency_key, bag_id, message_type, from_state, to_state,
                                                     outcome, scanner_id, raw_text, payload, occurred_at_utc, received_at_utc)
                         VALUES (@eid, @ik, @bid, @mt, @from, @to, @oc, @sc, @raw, @pl, @occ, @rec)
                         """,
                         P("eid", Guid.NewGuid()), P("ik", ev.IdempotencyKey), P("bid", bagId), P("mt", ev.MessageType),
                         P("from", current is null ? null : StateNames.ToDb(current.Value)),
                         P("to", StateNames.ToDb(stale ? ev.TargetState : newState)),
                         P("oc", outcome), P("sc", ev.ScanPoint), P("raw", rawText), J("pl", payload),
                         P("occ", ev.OccurredAtUtc), P("rec", received)))
        {
            await evCmd.ExecuteNonQueryAsync(ct);
        }

        if (!stale)
        {
            var outboxPayload = JsonSerializer.Serialize(new
            {
                bagTag = ev.BagTag,
                flightId,
                fromState = current is null ? null : StateNames.ToDb(current.Value),
                toState = StateNames.ToDb(newState),
                outcome,
                occurredAtUtc = ev.OccurredAtUtc,
            });
            await using var ob = Cmd(conn, tx,
                "INSERT INTO outbox_messages (id, topic, partition_key, payload, created_at_utc) VALUES (@id, 'baggage.events', @pk, @pl, @now)",
                P("id", Guid.NewGuid()), P("pk", ev.BagTag), J("pl", outboxPayload), P("now", received));
            await ob.ExecuteNonQueryAsync(ct);

            if (newState == BagState.ClaimedFromBelt)
            {
                await using var dl = Cmd(conn, tx,
                    "INSERT INTO exit_deadlines (bag_id, due_at_utc) VALUES (@id, @due) ON CONFLICT (bag_id) DO NOTHING",
                    P("id", bagId), P("due", ev.OccurredAtUtc + ExitDeadline)); // olay zamanına bağlı: sanal saatte de doğru çalışır
                await dl.ExecuteNonQueryAsync(ct);
            }
            else if (newState == BagState.ExitCleared)
            {
                await using var dl = Cmd(conn, tx,
                    "UPDATE exit_deadlines SET status = 'CLEARED' WHERE bag_id = @id",
                    P("id", bagId));
                await dl.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        return new ProcessResult(ProcessResult.Accepted, outcome,
            current is null ? null : StateNames.ToDb(current.Value),
            StateNames.ToDb(stale ? current!.Value : newState), ev.BagTag, flightId, null);
    }

    private async Task DeadLetterAsync(string source, string? rawText, string error, CancellationToken ct)
    {
        var raw = rawText ?? string.Empty;
        if (raw.Length > 2000)
        {
            raw = raw[..2000];
        }

        await using var cmd = db.CreateCommand(
            "INSERT INTO dead_letter_messages (id, source, raw_text, error) VALUES (@id, @src, @raw, @err)");
        cmd.Parameters.Add(P("id", Guid.NewGuid()));
        cmd.Parameters.Add(P("src", source.Length > 32 ? source[..32] : source));
        cmd.Parameters.Add(P("raw", raw));
        cmd.Parameters.Add(P("err", error));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }

    private static NpgsqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    private static NpgsqlParameter PB(string name, byte[] bytes) => new(name, NpgsqlDbType.Bytea) { Value = bytes };

    private static NpgsqlParameter J(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };
}
