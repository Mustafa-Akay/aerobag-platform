using AeroBag.Application.Time;
using AeroBag.Application.Transfer;
using AeroBag.Infrastructure.Db;
using Npgsql;

namespace AeroBag.Infrastructure;

/// <summary>
/// Bir bağlantı uçuşunun aktarma bavulları (W10a). Sayaçlar bağlantı satırının ve gelen satırın durumundan türetilir:
/// <list type="bullet">
/// <item><c>NotArrived</c>: gelen uçuştan henüz indirilmedi (ya da gelen satırı hiç görülmedi).</item>
/// <item><c>AwaitingSort</c>: indirildi, aktarma hattından henüz geçmedi.</item>
/// <item><c>Sorted</c>: aktarma hattından geçti, bağlantı uçağına henüz yüklenmedi.</item>
/// <item><c>Loaded</c>: bağlantı uçağına yüklendi.</item>
/// <item><c>Offloaded</c>: yolcu uçmadı, bavul indirildi.</item>
/// </list>
/// </summary>
/// <param name="MinutesToCutoff">Operasyon saatine göre kabul kapanışına kalan dakika (geçtiyse eksi); bilinmiyorsa null.</param>
public sealed record TransferFlightSummary(
    string FlightId,
    DateTimeOffset? ScheduledDepartureUtc,
    DateTimeOffset? CutoffUtc,
    DateTimeOffset? DepartedUtc,
    double? MinutesToCutoff,
    int Total,
    int NotArrived,
    int AwaitingSort,
    int Sorted,
    int Loaded,
    int Offloaded);

public sealed class TransferQueries(NpgsqlDataSource db, TransferPolicy? policy = null, IOperationalClock? clock = null)
{
    private readonly TransferPolicy _policy = policy ?? TransferPolicy.Default;
    private readonly IOperationalClock _now = clock ?? new EventDerivedClock(db);

    public async Task<IReadOnlyList<TransferFlightSummary>> FlightsAsync(CancellationToken ct = default)
    {
        var now = await _now.NowAsync(ct);
        var result = new List<TransferFlightSummary>();
        await using var cmd = db.CreateCommand(
            """
            SELECT o.flight_id, m.scheduled_departure_utc, m.departed_utc,
                   count(*)::int,
                   (count(*) FILTER (WHERE o.current_state IN ('CHECKED_IN','SECURITY_SCREENED')
                                       AND (i.current_state IS NULL OR i.current_state IN ('CHECKED_IN','SECURITY_SCREENED','LOADED_ON_AIRCRAFT'))))::int,
                   (count(*) FILTER (WHERE o.current_state IN ('CHECKED_IN','SECURITY_SCREENED')
                                       AND i.current_state IN ('UNLOADED_AT_ARRIVAL','TRANSFERRED')))::int,
                   (count(*) FILTER (WHERE o.current_state = 'TRANSFER_SORTED'))::int,
                   (count(*) FILTER (WHERE o.current_state NOT IN ('CHECKED_IN','SECURITY_SCREENED','TRANSFER_SORTED','OFFLOADED_NO_SHOW')))::int,
                   (count(*) FILTER (WHERE o.current_state = 'OFFLOADED_NO_SHOW'))::int
            FROM bags o
            LEFT JOIN bags i ON i.bag_tag_number = o.bag_tag_number AND i.flight_id = o.inbound_flight_id
            LEFT JOIN flight_milestones m ON m.flight_id = o.flight_id
            WHERE o.inbound_flight_id IS NOT NULL
            GROUP BY o.flight_id, m.scheduled_departure_utc, m.departed_utc
            ORDER BY m.scheduled_departure_utc NULLS LAST, o.flight_id
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            DateTimeOffset? std = r.GetNullableDateTimeOffset(1);
            DateTimeOffset? departed = r.GetNullableDateTimeOffset(2);
            var cutoff = _policy.CutoffFor(std);
            double? toCutoff = cutoff is { } c && now is { } n ? Math.Round((c - n).TotalMinutes, 1) : null;
            result.Add(new TransferFlightSummary(r.GetString(0), std, cutoff, departed, toCutoff,
                r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)));
        }
        return result;
    }
}
