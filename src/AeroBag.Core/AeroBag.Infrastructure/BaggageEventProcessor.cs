using System.Text.Json;
using AeroBag.Application.Coverage;
using AeroBag.Application.TypeB;
using AeroBag.Domain;
using AeroBag.Infrastructure.Db;
using Npgsql;
using static AeroBag.Infrastructure.Db.DbParams;

namespace AeroBag.Infrastructure;

/// <summary>
/// Ham Type B mesajını alır; ayrıştırır, durum makinesinden geçirir ve tek bir veritabanı
/// işleminde olay günlüğü + bavul durumu + outbox (+ çıkış süresi) kayıtlarını yazar.
/// Ayrıştırılamayan mesaj ölü mektup tablosuna gider. Aynı bavul için işlemler
/// advisory lock ile sıraya dizilir; aynı idempotency_key ikinci kez işlenmez.
/// </summary>
/// <remarks>
/// Okuma kapsamı (W5): atlanan bir adım yalnızca o noktada okuyucu varsa "sıra dışı" (ANOMALY) sayılır. Bant ağzında
/// okuyucu olmayan bir kurulumda şuttan çıkışa geçmek beklenen akıştır.
/// <para>
/// Aktarma (W10a): bavulun gelen uçuştaki satırı ile bağlantı uçuşundaki satırı ayrı satırlardır ve birbirini gösterir
/// (<c>onward_flight_id</c>, <c>inbound_flight_id</c>). Bağlantı satırı aktarma hattından geçince ya da yüklenince gelen satır
/// TRANSFERRED ile kapanır (sistem olayı). Bunu yalnızca bağlantı satırını işleyen işlem yapar ve kilitleri hep aynı sırada
/// alır (önce bağlantı, sonra gelen satır); gelen satırı işleyen işlem bağlantı satırına dokunmaz, böylece kilitlenme olmaz.
/// </para>
/// </remarks>
public sealed class BaggageEventProcessor(NpgsqlDataSource db, TimeProvider clock, LookupKeyHasher hasher, ReadCoverage? coverage = null)
{
    private readonly ReadCoverage _coverage = coverage ?? ReadCoverage.Full;

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

        return await StoreAsync(ev!, rawText ?? string.Empty, received, source, ct);
    }

    private async Task<ProcessResult> StoreAsync(BaggageEvent ev, string rawText, DateTimeOffset received, string source, CancellationToken ct)
    {
        var flightId = $"{ev.FlightNumber}-{ev.FlightDate:yyyyMMdd}";

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await AdvisoryLock.ForBagAsync(conn, tx, ev.BagTag, flightId, ct);

        await using (var dupCmd = Cmd(conn, tx, "SELECT 1 FROM baggage_events WHERE idempotency_key = @k",
                         P("k", ev.IdempotencyKey)))
        {
            if (await dupCmd.ExecuteScalarAsync(ct) is not null)
            {
                return ProcessResult.Dup(ev.BagTag, flightId);
            }
        }

        // Uçuş bağlamı normalde AODB'den gelir; şimdilik bilinmeyen alanlar yer tutucudur. Varış havalimanı mesajın .F satırından
        // (W10a: bağlantı uçuşunun gideceği yer yolcu kartında gösterilir); uçuş satırı yalnızca ilk mesajla yazılır.
        await using (var flightCmd = Cmd(conn, tx,
                         """
                         INSERT INTO flights (flight_id, airline_code, flight_number, flight_date,
                                              aircraft_type, origin_airport, destination_airport, is_international)
                         VALUES (@id, @al, @fn, @fd, 'UNK', 'XXX', @dest, FALSE)
                         ON CONFLICT (flight_id) DO NOTHING
                         """,
                         P("id", flightId), P("al", ev.FlightNumber[..2]), P("fn", ev.FlightNumber), P("fd", ev.FlightDate),
                         P("dest", ev.DestinationAirport ?? "XXX")))
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
                             INSERT INTO passengers (passenger_id, flight_id, lookup_key, pnr_key, cabin_class)
                             VALUES (@id, @f, @k, @pk, @c)
                             ON CONFLICT (flight_id, lookup_key) DO NOTHING
                             """,
                             P("id", Guid.NewGuid()), P("f", flightId), PB("k", lookupKey), PB("pk", hasher.ComputePnr(ev.Pnr)), P("c", ev.CabinClass ?? "Y")))
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
        string? inboundFlightId = ev.InboundFlightId;
        string? storedInbound = null;
        await using (var selCmd = Cmd(conn, tx,
                         "SELECT bag_id, current_state, inbound_flight_id FROM bags WHERE bag_tag_number = @t AND flight_id = @f",
                         P("t", ev.BagTag), P("f", flightId)))
        {
            await using var reader = await selCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                bagExists = true;
                bagId = reader.GetGuid(0);
                current = StateNames.FromDb(reader.GetString(1));
                if (!reader.IsDBNull(2))
                {
                    storedInbound = reader.GetString(2);
                    inboundFlightId = storedInbound; // kayıtlı bağ önceliklidir
                }
            }
        }

        if (inboundFlightId is null)
        {
            // Aktarma BSM'si kaybolmuş ya da gecikmiş olabilir: gelen uçuştaki satır (.O ile) bu uçuşu bağlantı olarak
            // gösteriyorsa bağ oradan öğrenilir. Gelen satır yalnızca okunur, kilidi alınmaz.
            await using var inferCmd = Cmd(conn, tx,
                "SELECT flight_id FROM bags WHERE onward_flight_id = @f AND bag_tag_number = @t ORDER BY updated_at_utc DESC LIMIT 1",
                P("f", flightId), P("t", ev.BagTag));
            inboundFlightId = await inferCmd.ExecuteScalarAsync(ct) as string;
        }

        // Bağlantı uçuşundaki satır (gelen uçuşu biliniyorsa) aktarma zincirini izler.
        var route = inboundFlightId is not null ? BagRoute.TransferOnward : BagRoute.Arrival;
        var transition = BagStateMachine.Apply(current, ev.TargetState, route);
        var stale = transition.Outcome == TransitionOutcome.IgnoredStale;
        var outcome = transition.Outcome switch
        {
            TransitionOutcome.Applied => "APPLIED",
            TransitionOutcome.AppliedWithAnomaly => _coverage.IsAnomaly(transition.SkippedStates) ? "ANOMALY" : "APPLIED",
            _ => "IGNORED_STALE",
        };
        var newState = transition.NewState;
        var exitCleared = newState == BagState.ExitCleared;

        if (!bagExists)
        {
            await using var ins = Cmd(conn, tx,
                """
                INSERT INTO bags (bag_id, bag_tag_number, flight_id, passenger_id, current_state, state_version, exit_cleared, updated_at_utc,
                                  inbound_flight_id, onward_flight_id)
                VALUES (@id, @t, @f, @p, @s, 1, @ec, @now, @inb, @onw)
                """,
                P("id", bagId), P("t", ev.BagTag), P("f", flightId), P("p", passengerId), P("s", StateNames.ToDb(newState)),
                P("ec", exitCleared), P("now", received), V("inb", inboundFlightId), V("onw", ev.OnwardFlightId));
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

        if (bagExists && ((storedInbound is null && inboundFlightId is not null) || ev.OnwardFlightId is not null))
        {
            // Aktarma bağı sonradan gelen bir mesajla (örn. geç gelen BSM) öğrenilebilir; kayıtlı bağ değiştirilmez.
            await using var links = Cmd(conn, tx,
                """
                UPDATE bags SET inbound_flight_id = COALESCE(inbound_flight_id, @inb),
                                onward_flight_id  = COALESCE(onward_flight_id, @onw)
                WHERE bag_id = @id
                """,
                V("inb", storedInbound is null ? inboundFlightId : null), V("onw", ev.OnwardFlightId), P("id", bagId));
            await links.ExecuteNonQueryAsync(ct);
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
                                                     outcome, scanner_id, source, raw_text, payload, occurred_at_utc, received_at_utc)
                         VALUES (@eid, @ik, @bid, @mt, @from, @to, @oc, @sc, @src, @raw, @pl, @occ, @rec)
                         """,
                         P("eid", Guid.NewGuid()), P("ik", ev.IdempotencyKey), P("bid", bagId), P("mt", ev.MessageType),
                         P("from", current is null ? null : StateNames.ToDb(current.Value)),
                         P("to", StateNames.ToDb(stale ? ev.TargetState : newState)),
                         P("oc", outcome), P("sc", ev.ScanPoint), P("src", source.Length > 32 ? source[..32] : source), P("raw", TypeBRedactor.Redact(rawText)), J("pl", payload),
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
            await Outbox.InsertAsync(conn, tx, OutboxTopics.BaggageEvents, ev.BagTag, outboxPayload, received, ct);

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

        if (route == BagRoute.TransferOnward && inboundFlightId is not null)
        {
            await LinkInboundAsync(conn, tx, ev.BagTag, inboundFlightId, flightId,
                handedOver: !stale && newState is BagState.TransferSorted or BagState.LoadedOnAircraft,
                ev.OccurredAtUtc, received, ct);
        }

        await tx.CommitAsync(ct);
        return new ProcessResult(ProcessResult.Accepted, outcome,
            current is null ? null : StateNames.ToDb(current.Value),
            StateNames.ToDb(stale ? current!.Value : newState), ev.BagTag, flightId, null);
    }

    /// <summary>
    /// Gelen uçuştaki satırı bağlantı satırına bağlar ve bavul aktarma hattına ya da bağlantı uçağına geçtiyse onu
    /// TRANSFERRED ile kapatır (SYSTEM olayı; aynı satır için bir kez). Gelen satır yoksa (BSM'si gelmemiş) bir şey yapılmaz.
    /// </summary>
    private async Task LinkInboundAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string tag, string inboundFlightId,
        string onwardFlightId, bool handedOver, DateTimeOffset occurred, DateTimeOffset received, CancellationToken ct)
    {
        // Kilit sırası: bağlantı satırının kilidi zaten alındı; şimdi gelen satırınki (bkz. sınıf açıklaması).
        await AdvisoryLock.ForBagAsync(conn, tx, tag, inboundFlightId, ct);

        Guid inboundBagId;
        BagState inboundState;
        await using (var sel = Cmd(conn, tx,
                         "SELECT bag_id, current_state FROM bags WHERE bag_tag_number = @t AND flight_id = @f FOR UPDATE",
                         P("t", tag), P("f", inboundFlightId)))
        {
            await using var r = await sel.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return;
            }
            inboundBagId = r.GetGuid(0);
            inboundState = StateNames.FromDb(r.GetString(1))!.Value;
        }

        await using (var link = Cmd(conn, tx,
                         "UPDATE bags SET onward_flight_id = COALESCE(onward_flight_id, @onw) WHERE bag_id = @id",
                         V("onw", onwardFlightId), P("id", inboundBagId)))
        {
            await link.ExecuteNonQueryAsync(ct);
        }

        if (!handedOver)
        {
            return;
        }

        var t = BagStateMachine.Apply(inboundState, BagState.Transferred);
        if (t.Outcome == TransitionOutcome.IgnoredStale)
        {
            return;
        }

        var outcome = t.Outcome == TransitionOutcome.AppliedWithAnomaly && _coverage.IsAnomaly(t.SkippedStates) ? "ANOMALY" : "APPLIED";
        await ExecAsync(conn, tx, ct,
            """
            UPDATE bags SET current_state = 'TRANSFERRED', state_version = state_version + 1, updated_at_utc = @now
            WHERE bag_id = @id
            """,
            P("now", received), P("id", inboundBagId));

        await ExecAsync(conn, tx, ct,
            """
            INSERT INTO baggage_events (event_id, idempotency_key, bag_id, message_type, from_state, to_state, outcome,
                                        scanner_id, source, raw_text, payload, occurred_at_utc, received_at_utc)
            VALUES (@eid, @ik, @bid, 'SYSTEM', @from, 'TRANSFERRED', @oc, 'transfer', 'system', NULL, @pl, @occ, @rec)
            ON CONFLICT (idempotency_key) DO NOTHING
            """,
            P("eid", Guid.NewGuid()), P("ik", $"transferred:{inboundBagId}"), P("bid", inboundBagId),
            P("from", StateNames.ToDb(inboundState)), P("oc", outcome),
            J("pl", JsonSerializer.Serialize(new
            {
                reason = "HANDED_TO_TRANSFER",
                onwardFlightId,
                skippedStates = t.SkippedStates.Select(StateNames.ToDb).ToArray(),
            })),
            P("occ", occurred), P("rec", received));

        await Outbox.InsertAsync(conn, tx, OutboxTopics.BaggageEvents, tag,
            JsonSerializer.Serialize(new
            {
                bagTag = tag,
                flightId = inboundFlightId,
                fromState = StateNames.ToDb(inboundState),
                toState = "TRANSFERRED",
                outcome,
                occurredAtUtc = occurred,
            }),
            received, ct);
    }

    private async Task DeadLetterAsync(string source, string? rawText, string error, CancellationToken ct)
    {
        // Ham metin ve hata metni kişisel alan (.P ad, .L PNR) içermesin diye maskelenir.
        var raw = TypeBRedactor.Redact(rawText);
        error = TypeBRedactor.ScrubText(error, rawText);
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
}
