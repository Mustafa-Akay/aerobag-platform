using AeroBag.Application.Alerts;
using AeroBag.Application.Coverage;
using AeroBag.Application.Eta;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Domain;
using AeroBag.Infrastructure.Db;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure;

public sealed record EtaView(
    DateTimeOffset P10Utc, DateTimeOffset P50Utc, DateTimeOffset P90Utc,
    int P10SecondsFromNow, int P50SecondsFromNow, int P90SecondsFromNow,
    string Basis);

/// <param name="Carousel">Uçuşa atanan bagaj bandı (AODB bağlamı); henüz atanmadıysa null.</param>
/// <param name="Notice">
/// Yolcuya gösterilecek bildirim (W4): NOT_LOADED ya da MISSING_AT_ARRIVAL (açık uyarı; bu durumda ETA verilmez)
/// veya ARRIVAL_DELAYED (indirme beklenenden geç başladı). Yoksa null.
/// </param>
/// <param name="OnwardFlightId">
/// Aktarma bavulu (W10a): bağlantı uçuşu. Doluysa kart tek bir yolculuğu gösterir: <paramref name="State"/> gelen uçuştaki satırın,
/// <paramref name="OnwardState"/> bağlantı uçuşundaki satırın durumudur (aktarma BSM'si henüz gelmediyse null). Aktarma bavulunda
/// bant, varış tahmini ve varış bildirimleri gösterilmez (bavul bu havalimanında banda gelmez).
/// </param>
/// <param name="OnwardDestination">Bağlantı uçuşunun varış havalimanı (IATA kodu); bilinmiyorsa null. Takip burada biter,
/// yolcuya bavulunu nerede alacağı söylenir.</param>
/// <param name="ConnectionMissed">
/// Bağlantı uçağı kalktı (AODB kalkış kilometre taşı, operasyon saatine göre) ve bavul ona yüklenmedi. Olasılık değil, olmuş bir
/// olaydır; yolcuya bu yüzden gösterilir. Kabul kapanışının geçmesi yalnızca personel panosunda görünür.
/// </param>
public sealed record PassengerBagView(string BagTag, string FlightId, string State, EtaView? Eta, string? Carousel, string? Notice,
    string? OnwardFlightId = null, string? OnwardState = null, string? OnwardDestination = null, bool ConnectionMissed = false)
{
    /// <summary>Yolcu jetonuna girecek (etiket, uçuş) çiftleri: aktarma bavulunda iki uçuşun satırı da.</summary>
    public IEnumerable<(string BagTag, string FlightId)> Keys()
    {
        yield return (BagTag, FlightId);
        if (OnwardFlightId is { } o && o != FlightId)
        {
            yield return (BagTag, o);
        }
    }
}

/// <param name="AsOfUtc">
/// ETA'ların göreli hesaplandığı an: operasyon saati (IOperationalClock). Simülasyonda son olay zamanı, gerçek kurulumda gerçek saat.
/// </param>
public sealed record LookupResult(DateTimeOffset AsOfUtc, IReadOnlyList<PassengerBagView> Bags);

public sealed class PassengerQueries(
    NpgsqlDataSource db, LookupKeyHasher hasher, EtaStatistics stats, TimeProvider clock,
    IMlPredictor? ml = null, FlightContextStore? contexts = null, IOperationalClock? operationalClock = null,
    AlertPolicy? alertPolicy = null, ReadCoverage? coverage = null)
{
    private readonly IOperationalClock _now = operationalClock ?? new EventDerivedClock(db);
    private readonly AlertPolicy _policy = alertPolicy ?? AlertPolicy.Default;
    private readonly ReadCoverage _coverage = coverage ?? ReadCoverage.Full;

    private record Row(string Tag, string Flight, string State, DateTimeOffset? LastEvent, string? Carousel,
        string? OpenAlert, DateTimeOffset? OnBlock, int FirstBagTargetSec, bool AnyBagUnloaded, DateTimeOffset? UnloadingCompleted,
        string? InboundFlight, string? OnwardFlight, string? Destination, DateTimeOffset? Departed)
    {
        public bool IsTransfer => InboundFlight is not null || OnwardFlight is not null;
    }

    /// <summary>
    /// PNR ve soyada ait bavulları döndürür; kayıt yoksa ya da soyad yanlışsa aynı şekilde null.
    /// Havayolları PNR'ları zamanla yeniden kullanır: aynı PNR ve soyad birden çok seyahatle eşleşirse yalnızca en son
    /// uçuş tarihindeki bavullar döner. Böylece PNR'ı bilen biri eski bir seyahatin bavullarını göremez (veri en aza indirme).
    /// Aktarmada bağlantı uçuşu ertesi güne düşebilir; o zaman o bağlantının gelen uçuştaki satırı da aynı seyahattir ve döner.
    /// </summary>
    public async Task<LookupResult?> LookupAsync(string pnr, string surname, CancellationToken ct = default)
    {
        var key = hasher.Compute(pnr, surname);
        var rows = new List<Row>();

        await using (var cmd = db.CreateCommand(
            """
            WITH latest AS (
                SELECT max(f2.flight_date) AS d FROM passengers p2
                JOIN flights f2 ON f2.flight_id = p2.flight_id
                WHERE p2.lookup_key = @k
            )
            SELECT b.bag_tag_number, b.flight_id, b.current_state,
                   (SELECT max(e.occurred_at_utc) FROM baggage_events e
                     WHERE e.bag_id = b.bag_id AND e.to_state = b.current_state AND e.outcome <> 'IGNORED_STALE'),
                   fc.carousel,
                   (SELECT a.kind FROM bag_alerts a WHERE a.bag_id = b.bag_id AND a.closed_at_utc IS NULL
                     ORDER BY a.opened_at_utc DESC LIMIT 1),
                   fc.on_block_utc,
                   f.sla_first_bag_sec,
                   EXISTS (SELECT 1 FROM bags o WHERE o.flight_id = b.flight_id
                            AND o.current_state NOT IN ('CHECKED_IN', 'SECURITY_SCREENED', 'LOADED_ON_AIRCRAFT', 'OFFLOADED_NO_SHOW')),
                   m.unloading_completed_utc,
                   b.inbound_flight_id, b.onward_flight_id,
                   NULLIF(f.destination_airport, 'XXX'),
                   m.departed_utc
            FROM passengers p
            JOIN bags b ON b.passenger_id = p.passenger_id
            JOIN flights f ON f.flight_id = b.flight_id
            LEFT JOIN flight_context fc ON fc.flight_id = b.flight_id
            LEFT JOIN flight_milestones m ON m.flight_id = b.flight_id
            WHERE p.lookup_key = @k
              AND (f.flight_date = (SELECT d FROM latest)
                   OR EXISTS (SELECT 1 FROM bags o
                              JOIN passengers po ON po.passenger_id = o.passenger_id
                              JOIN flights fo ON fo.flight_id = o.flight_id
                              WHERE po.lookup_key = @k AND o.bag_tag_number = b.bag_tag_number
                                AND o.flight_id = b.onward_flight_id AND fo.flight_date = (SELECT d FROM latest)))
            ORDER BY b.bag_tag_number, f.flight_date, b.flight_id
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Bytea) { Value = key });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(
                    r.GetString(0).Trim(), r.GetString(1), r.GetString(2),
                    r.GetNullableDateTimeOffset(3),
                    r.GetNullableString(4),
                    r.GetNullableString(5),
                    r.GetNullableDateTimeOffset(6),
                    r.GetInt32(7),
                    r.GetBoolean(8),
                    r.GetNullableDateTimeOffset(9),
                    r.GetNullableString(10),
                    r.GetNullableString(11),
                    r.GetNullableString(12)?.Trim(),
                    r.GetNullableDateTimeOffset(13)));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // Operasyon saati yoksa (henüz hiç olay yok) gerçek saate düşülür; o durumda gösterilecek bir ETA da yoktur.
        var asOf = await _now.NowAsync(ct) ?? clock.GetUtcNow();
        var unloadToCarousel = await stats.UnloadToCarouselAsync(ct);
        var chuteToCarousel = await stats.ChuteToCarouselAsync(ct);

        // ML ve fizik tahmini için uçuş bağlamı gerekir. Servis kapalıysa ya da bağlam yoksa eskisi gibi davranılır
        // (iniş öncesinde ETA yok; indirme ve şut aşamasında ampirik ETA).
        var preWindows = new Dictionary<string, EtaWindow?>();
        var bagForecasts = new Dictionary<string, BagDurationForecast?>();
        var ctxs = new Dictionary<string, FlightContext>();
        if (contexts is not null)
        {
            var flightsNeeding = rows
                .Where(r => !r.IsTransfer)
                .Where(r => BagStateMachine.Rank(StateNames.FromDb(r.State)!.Value) < BagStateMachine.Rank(BagState.OnCarousel))
                .Select(r => r.Flight).Distinct().ToList();
            ctxs = await contexts.GetAsync(flightsNeeding, ct);

            if (ml is not null)
            {
                foreach (var row in rows.Where(r => !r.IsTransfer))
                {
                    var st = StateNames.FromDb(row.State)!.Value;
                    if (!ctxs.TryGetValue(row.Flight, out var c))
                    {
                        continue;
                    }
                    if (BagStateMachine.Rank(st) < BagStateMachine.Rank(BagState.UnloadedAtArrival) && !preWindows.ContainsKey(row.Flight))
                    {
                        preWindows[row.Flight] = await ml.PredictAsync(c, ct) is { } f ? MlEtaWindow.Build(c, f, asOf) : null;
                    }
                    else if (st == BagState.UnloadedAtArrival && !bagForecasts.ContainsKey(row.Flight))
                    {
                        bagForecasts[row.Flight] = await ml.PredictBagAsync(c, ct);
                    }
                }
            }
        }

        var bags = new List<PassengerBagView>();
        bags.AddRange(TransferViews(rows, asOf));
        bags.AddRange(rows.Where(r => !r.IsTransfer).Select(row =>
        {
            var state = StateNames.FromDb(row.State)!.Value;
            EtaWindow? window = null;
            if (BagStateMachine.Rank(state) < BagStateMachine.Rank(BagState.UnloadedAtArrival))
            {
                preWindows.TryGetValue(row.Flight, out window);
            }
            else if (row.LastEvent is { } last)
            {
                if (state == BagState.UnloadedAtArrival && bagForecasts.TryGetValue(row.Flight, out var bf) && bf is not null)
                {
                    window = MlEtaWindow.BuildForBag(last, bf);
                }
                else if (state == BagState.InboundChute && ctxs.TryGetValue(row.Flight, out var pc))
                {
                    window = PhysicsEtaWindow.Build(last, pc);
                }

                window ??= EtaEstimator.Estimate(state, last, unloadToCarousel, chuteToCarousel);
            }
            // Açık uyarı varsa ETA gösterilmez: "bantta görünecek" demek yanıltıcı olur. Uyarı işçisi birkaç saniyede bir
            // çalıştığı için bavul bu arada okunduysa eski uyarı gösterilmez. NOT_LOADED yolcuya ancak varışta gösterilir
            // (VisibleToPassenger; personel kalkıştan sonra görür).
            string? notice = null;
            if (row.OpenAlert is { } alert && BagAlertRules.VisibleToPassenger(alert, state, row.UnloadingCompleted, asOf, _policy,
                    _coverage.HasReadAfterUnload))
            {
                notice = alert;
                window = null;
            }
            else if (BagAlertRules.IsArrivalDelayed(state, row.OnBlock, row.AnyBagUnloaded, row.FirstBagTargetSec, asOf))
            {
                notice = BagNotice.ArrivalDelayed;
            }
            else if (!_coverage.Has(ReadPoint.Carousel)
                     && state is BagState.UnloadedAtArrival or BagState.InboundChute
                     && window is not null && asOf >= window.P90Utc)
            {
                // Bant ağzı okuması bu kurulumda hiç gelmeyecek: aralık geçince "büyük olasılıkla bantta" (W5).
                notice = BagNotice.LikelyOnBelt;
                window = null;
            }
            return new PassengerBagView(row.Tag, row.Flight, row.State, window is null ? null : ToView(window, asOf), row.Carousel, notice);
        }));

        return new LookupResult(asOf, bags.OrderBy(b => b.BagTag, StringComparer.Ordinal).ThenBy(b => b.FlightId, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Aktarma bavulunun iki satırını (gelen uçuş ve bağlantı uçuşu) tek bir karta birleştirir. Bağlantı satırının gelen
    /// satırı bu sorguda yoksa (örn. gelen uçuşun BSM'si farklı bir PNR'la geldiyse) bağlantı satırı tek başına gösterilir.
    /// Varış bildirimleri (W4) bu kartta gösterilmez: metinleri yolcuyu bant başına ve bagaj hizmetlerine yönlendirir,
    /// aktarma yolcusu için yanlıştır. Aktarmaya özgü bildirimler W10b'dedir.
    /// </summary>
    private static IEnumerable<PassengerBagView> TransferViews(List<Row> rows, DateTimeOffset asOf)
    {
        // Bağlantı uçağı kalktı ve bavul yüklenmedi (yolcu uçmadıysa bavulun indirilmesi ayrı bir durumdur).
        static bool Missed(Row onward, DateTimeOffset now)
        {
            var s = StateNames.FromDb(onward.State)!.Value;
            return onward.Departed is { } d && now >= d
                && s != BagState.OffloadedNoShow
                && BagStateMachine.Rank(s) < BagStateMachine.Rank(BagState.LoadedOnAircraft);
        }

        var onwardRows = rows.Where(r => r.InboundFlight is not null).ToList();
        var usedOnward = new HashSet<Row>();
        foreach (var inbound in rows.Where(r => r.InboundFlight is null && r.OnwardFlight is not null))
        {
            var onward = onwardRows.FirstOrDefault(o => o.Tag == inbound.Tag && o.Flight == inbound.OnwardFlight && o.InboundFlight == inbound.Flight);
            if (onward is not null)
            {
                usedOnward.Add(onward);
            }
            yield return new PassengerBagView(inbound.Tag, inbound.Flight, inbound.State, null, null, null,
                inbound.OnwardFlight, onward?.State, onward?.Destination, onward is not null && Missed(onward, asOf));
        }

        foreach (var onward in onwardRows.Where(o => !usedOnward.Contains(o)))
        {
            // Gelen satır bu sorguda yok: bağlantı satırı kendi başına (gelen ayağın durumu bilinmiyor).
            yield return new PassengerBagView(onward.Tag, onward.Flight, onward.State, null, null, null, onward.Flight, onward.State,
                onward.Destination, Missed(onward, asOf));
        }
    }

    private static EtaView ToView(EtaWindow w, DateTimeOffset asOf)
    {
        static int Secs(DateTimeOffset t, DateTimeOffset now) => (int)Math.Max(0, Math.Round((t - now).TotalSeconds));
        return new EtaView(w.P10Utc, w.P50Utc, w.P90Utc,
            Secs(w.P10Utc, asOf), Secs(w.P50Utc, asOf), Secs(w.P90Utc, asOf), w.Basis);
    }
}
