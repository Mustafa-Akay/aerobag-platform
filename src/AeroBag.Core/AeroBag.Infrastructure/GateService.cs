using System.Text.Json;
using AeroBag.Application.ExitGuard;
using AeroBag.Domain;
using AeroBag.Infrastructure.Db;
using Npgsql;
using NpgsqlTypes;
using static AeroBag.Infrastructure.Db.DbParams;

namespace AeroBag.Infrastructure;

public sealed record ExitGuardOptions(bool Enabled, double MinSignalQuality);

/// <summary>Kapı sensör paketi (ARCHITECTURE.md 4.3). Sunulan PNR yalnızca bellekte özetlenir, hiçbir yerde saklanmaz.</summary>
public sealed record GateScan(
    string GateId, DateTimeOffset TimestampUtc, IReadOnlyList<string> BagTags, string? Method, string? PresentedPnr, double? SignalQuality);

public sealed record GateTagResult(string BagTag, string Decision, string? State);

/// <summary>
/// ExitGuard: kapıda okunan her bavul için karar verir ve sonucu yazar.
/// Her okuma <c>gate_reads</c>'e (denetim izi) yazılır; durum değiştiren kararlar ayrıca olay günlüğüne ve outbox'a,
/// uyuşmazlık ve sıra dışı okumalar inceleme kaydına (<c>exit_reviews</c>) yazılır. Aynı paket ikinci kez gelirse DUPLICATE.
/// </summary>
public sealed class GateService(NpgsqlDataSource db, LookupKeyHasher hasher, ExitGuardOptions options,
    AeroBag.Application.Coverage.ReadCoverage? coverage = null)
{
    public const string UnknownTag = "UNKNOWN_TAG";
    public const string Duplicate = "DUPLICATE";

    private readonly AeroBag.Application.Coverage.ReadCoverage _coverage = coverage ?? AeroBag.Application.Coverage.ReadCoverage.Full;

    public async Task<IReadOnlyList<GateTagResult>> ProcessAsync(GateScan scan, string source, CancellationToken ct = default)
    {
        var presented = string.IsNullOrWhiteSpace(scan.PresentedPnr) ? null : hasher.ComputePnr(scan.PresentedPnr);
        var results = new List<GateTagResult>();
        foreach (var tag in scan.BagTags.Distinct(StringComparer.Ordinal))
        {
            results.Add(await ProcessTagAsync(scan, tag, presented, source, ct));
        }
        return results;
    }

    private async Task<GateTagResult> ProcessTagAsync(GateScan scan, string tag, byte[]? presented, string source, CancellationToken ct)
    {
        var ik = $"GATE:{scan.GateId}:{tag}:{scan.TimestampUtc.UtcDateTime:yyyyMMddTHHmmssfff}";
        var now = DateTimeOffset.UtcNow;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Etiketler zamanla yeniden kullanılabilir: en son güncellenen bavul alınır.
        Guid? bagId = null;
        string? flightId = null;
        await using (var find = Cmd(conn, tx,
                         "SELECT bag_id, flight_id FROM bags WHERE bag_tag_number = @t ORDER BY updated_at_utc DESC LIMIT 1",
                         P("t", tag)))
        {
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                bagId = r.GetGuid(0);
                flightId = r.GetString(1);
            }
        }

        if (bagId is null)
        {
            var readId = await InsertReadAsync(conn, tx, ik, scan, tag, null, UnknownTag, "UNKNOWN_TAG", source, ct);
            if (readId is null)
            {
                return new GateTagResult(tag, Duplicate, null);
            }
            await InsertReviewAsync(conn, tx, readId.Value, null, tag, null, scan, "UNKNOWN_TAG", ct);
            await tx.CommitAsync(ct);
            return new GateTagResult(tag, UnknownTag, null);
        }

        await AdvisoryLock.ForBagAsync(conn, tx, tag, flightId!, ct);

        await using (var dup = Cmd(conn, tx, "SELECT 1 FROM gate_reads WHERE idempotency_key = @k", P("k", ik)))
        {
            if (await dup.ExecuteScalarAsync(ct) is not null)
            {
                return new GateTagResult(tag, Duplicate, null);
            }
        }

        BagState current;
        byte[]? ownerKey;
        await using (var sel = Cmd(conn, tx,
                         """
                         SELECT b.current_state, p.pnr_key
                         FROM bags b LEFT JOIN passengers p ON p.passenger_id = b.passenger_id
                         WHERE b.bag_id = @id
                         """, P("id", bagId)))
        {
            await using var r = await sel.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            current = StateNames.FromDb(r.GetString(0)) ?? throw new InvalidOperationException("Bavulun durumu tanınmadı.");
            ownerKey = r.GetNullableBytes(1);
        }

        var verdict = ExitGuardRules.Decide(current, ownerKey, presented, scan.SignalQuality, options.MinSignalQuality,
            _coverage.LastArrivalState);
        var gateReadId = await InsertReadAsync(conn, tx, ik, scan, tag, bagId, verdict.Code, verdict.Reason, source, ct)
            ?? throw new InvalidOperationException("Kapı okuması yazılamadı.");

        var finalState = current;
        if (verdict.Decision is GateDecision.ClearedOwner or GateDecision.ReviewMismatch)
        {
            var target = verdict.Decision == GateDecision.ClearedOwner ? BagState.ExitCleared : BagState.ExitReview;
            var transition = BagStateMachine.Apply(current, target);
            if (transition.Outcome != TransitionOutcome.IgnoredStale)
            {
                finalState = transition.NewState;
                await ApplyAsync(conn, tx, bagId.Value, tag, flightId!, current, transition, verdict, gateReadId, scan, source, now, _coverage, ct);
            }
        }

        if (verdict.Decision is GateDecision.ReviewMismatch or GateDecision.OutOfSequence)
        {
            await InsertReviewAsync(conn, tx, gateReadId, bagId, tag, flightId, scan, verdict.Reason, ct);
        }

        await tx.CommitAsync(ct);
        return new GateTagResult(tag, verdict.Code, StateNames.ToDb(finalState));
    }

    private static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid bagId, string tag, string flightId,
        BagState current, TransitionResult transition, GateVerdict verdict, Guid gateReadId, GateScan scan, string source,
        DateTimeOffset now, AeroBag.Application.Coverage.ReadCoverage readCoverage, CancellationToken ct)
    {
        var newState = transition.NewState;
        // Okuyucusu olmayan noktanın atlanması (ör. tipik kapsamda şuttan çıkışa) sıra dışı sayılmaz (W5).
        var outcome = transition.Outcome == TransitionOutcome.Applied || !readCoverage.IsAnomaly(transition.SkippedStates)
            ? "APPLIED" : "ANOMALY";

        await using (var upd = Cmd(conn, tx,
                         """
                         UPDATE bags SET current_state = @s, state_version = state_version + 1, exit_cleared = @ec, updated_at_utc = @now
                         WHERE bag_id = @id
                         """,
                         P("s", StateNames.ToDb(newState)), P("ec", newState == BagState.ExitCleared), P("now", now), P("id", bagId)))
        {
            await upd.ExecuteNonQueryAsync(ct);
        }

        var payload = JsonSerializer.Serialize(new
        {
            bagTag = tag,
            flightId,
            gateId = scan.GateId,
            gateReadId,
            decision = verdict.Code,
            reason = verdict.Reason,
            skippedStates = transition.SkippedStates.Select(StateNames.ToDb).ToArray(),
        });
        await using (var ev = Cmd(conn, tx,
                         """
                         INSERT INTO baggage_events (event_id, idempotency_key, bag_id, message_type, from_state, to_state,
                                                     outcome, scanner_id, source, raw_text, payload, occurred_at_utc, received_at_utc)
                         VALUES (@eid, @ik, @bid, 'GATE', @from, @to, @oc, @sc, @src, NULL, @pl, @occ, @rec)
                         """,
                         P("eid", Guid.NewGuid()), P("ik", $"GATEEV:{gateReadId}"), P("bid", bagId),
                         P("from", StateNames.ToDb(current)), P("to", StateNames.ToDb(newState)), P("oc", outcome),
                         P("sc", scan.GateId), P("src", source.Length > 32 ? source[..32] : source), J("pl", payload),
                         P("occ", scan.TimestampUtc), P("rec", now)))
        {
            await ev.ExecuteNonQueryAsync(ct);
        }

        var outboxPayload = JsonSerializer.Serialize(new
        {
            bagTag = tag,
            flightId,
            fromState = StateNames.ToDb(current),
            toState = StateNames.ToDb(newState),
            outcome,
            occurredAtUtc = scan.TimestampUtc,
        });
        await Outbox.InsertAsync(conn, tx, OutboxTopics.BaggageEvents, tag, outboxPayload, now, ct);

        await using (var dl = Cmd(conn, tx, "UPDATE exit_deadlines SET status = @st WHERE bag_id = @id AND status IN ('PENDING', 'OVERDUE')",
                         P("st", newState == BagState.ExitCleared ? "CLEARED" : "REVIEW"), P("id", bagId)))
        {
            await dl.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Kapı okumasını yazar; aynı anahtar varsa null döner (tekrar gelen paket).</summary>
    private static async Task<Guid?> InsertReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string ik, GateScan scan, string tag,
        Guid? bagId, string decision, string reason, string source, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            """
            INSERT INTO gate_reads (read_id, idempotency_key, gate_id, bag_tag, bag_id, decision, reason, signal_quality, method, source, occurred_at_utc)
            VALUES (@id, @ik, @g, @t, @b, @d, @r, @q, @m, @src, @occ)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING read_id
            """,
            P("id", Guid.NewGuid()), P("ik", ik), P("g", scan.GateId), P("t", tag), P("b", bagId), P("d", decision), P("r", reason),
            new NpgsqlParameter("q", NpgsqlDbType.Real) { Value = scan.SignalQuality is { } q ? (float)q : DBNull.Value },
            P("m", scan.Method), P("src", source.Length > 32 ? source[..32] : source), P("occ", scan.TimestampUtc));
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    private static async Task InsertReviewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid gateReadId, Guid? bagId, string tag,
        string? flightId, GateScan scan, string reason, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            """
            INSERT INTO exit_reviews (review_id, gate_read_id, bag_id, bag_tag, flight_id, gate_id, reason, occurred_at_utc)
            VALUES (@id, @gr, @b, @t, @f, @g, @r, @occ)
            """,
            P("id", Guid.NewGuid()), P("gr", gateReadId), P("b", bagId), P("t", tag), P("f", flightId), P("g", scan.GateId),
            P("r", reason), P("occ", scan.TimestampUtc));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record ExitReviewView(
    Guid Id, string BagTag, string? FlightId, string GateId, string Reason, DateTimeOffset OccurredAtUtc,
    string Status, string? Resolution, string? ResolvedBy, DateTimeOffset? ResolvedAtUtc, string? Note);

public enum ResolveOutcome { Resolved, NotFound, AlreadyResolved }

/// <summary>Güvenlik ekranı için inceleme kayıtları. Kişisel veri içermez (etiket, kapı, zaman, gerekçe).</summary>
public sealed class SecurityReviews(NpgsqlDataSource db, TimeProvider clock)
{
    public static readonly string[] Resolutions = ["CONFIRMED", "FALSE_ALARM"];

    public async Task<IReadOnlyList<ExitReviewView>> ListAsync(bool openOnly, int limit = 200, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT review_id, bag_tag, flight_id, gate_id, reason, occurred_at_utc, status, resolution, resolved_by, resolved_at_utc, note
            FROM exit_reviews
            WHERE (@open = FALSE OR status = 'OPEN')
            ORDER BY occurred_at_utc DESC
            LIMIT @n
            """);
        cmd.Parameters.Add(new NpgsqlParameter("open", NpgsqlDbType.Boolean) { Value = openOnly });
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = Math.Clamp(limit, 1, 500) });
        var list = new List<ExitReviewView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ExitReviewView(r.GetGuid(0), r.GetString(1).Trim(), r.GetNullableString(2), r.GetString(3),
                r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetNullableString(7),
                r.GetNullableString(8), r.GetNullableDateTimeOffset(9), r.GetNullableString(10)));
        }
        return list;
    }

    public async Task<ResolveOutcome> ResolveAsync(Guid id, string resolution, string? note, string resolvedBy, CancellationToken ct = default)
    {
        await using (var cmd = db.CreateCommand(
                         """
                         UPDATE exit_reviews SET status = 'RESOLVED', resolution = @r, resolved_by = @u, resolved_at_utc = @now, note = @n
                         WHERE review_id = @id AND status = 'OPEN'
                         """))
        {
            cmd.Parameters.AddWithValue("r", resolution);
            cmd.Parameters.AddWithValue("u", resolvedBy);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Varchar) { Value = (object?)note ?? DBNull.Value });
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 1)
            {
                return ResolveOutcome.Resolved;
            }
        }

        await using var exists = db.CreateCommand("SELECT 1 FROM exit_reviews WHERE review_id = @id");
        exists.Parameters.AddWithValue("id", id);
        return await exists.ExecuteScalarAsync(ct) is null ? ResolveOutcome.NotFound : ResolveOutcome.AlreadyResolved;
    }
}
