using AeroBag.Application.Time;
using AeroBag.Infrastructure.Db;
using Npgsql;

namespace AeroBag.Infrastructure;

/// <summary>
/// Simülasyon saati: BSM olmayan olayların en son zamanı. BSM'nin olay zamanı mesajda yoktur (alınma anıdır),
/// sanal saati bozmasın diye dışarıda bırakılır. Olay akışı durursa bu saat de durur. Gerçek kurulumda kullanılmaz.
/// Kodda "en son olay zamanı" ile şimdi hesaplayan tek yer burasıdır (bkz. OperationalClockGuardTests).
/// </summary>
public sealed class EventDerivedClock(NpgsqlDataSource db) : IOperationalClock
{
    public string Mode => ClockModes.Event;

    public async Task<DateTimeOffset?> NowAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM'");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return r.GetNullableDateTimeOffset(0);
    }
}
