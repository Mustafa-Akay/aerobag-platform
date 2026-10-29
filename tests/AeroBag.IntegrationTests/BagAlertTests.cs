using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AeroBag.Application.Alerts;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>
/// W4: kayıp bavul uyarıları. Uyarı işçisi API'nin kısıtlı rolüyle (aerobag_app) çalıştırılır; böylece yetkiler de doğrulanır.
/// Uyarılar tüm uçuşları tarar; her test kendi uçuşunun bavullarına bakar.
/// </summary>
public sealed class BagAlertTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero);
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private readonly NpgsqlDataSource _app = NpgsqlDataSource.Create(fx.AppConnectionString);

    public void Dispose() => _app.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTimeOffset At(string hhmmss) => DateTimeOffset.ParseExact($"20261022{hhmmss}", "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedTime(Wall), Hasher);

    private BagAlertService Alerts() => new(_app, AlertPolicy.Default);

    private PassengerQueries Lookup(DateTimeOffset now) =>
        new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedTime(Wall), contexts: new FlightContextStore(fx.Db),
            operationalClock: new FixedOperationalClock(now));

    private async Task<(string Tag, string Pnr)> BagAsync(string flightNo, params (string Point, string Time)[] scans)
    {
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync($"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM", "test");
        foreach (var (point, time) in scans)
        {
            await ScanAsync(flightNo, tag, point, time);
        }
        return (tag, pnr);
    }

    private async Task ScanAsync(string flightNo, string tag, string point, string time)
    {
        var text = point == "UNLOAD"
            ? $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T{time}Z\nENDBUM"
            : $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM";
        await Processor().ProcessAsync(text, "test");
    }

    private Task MilestonesAsync(string flightNo, string? departed, string? unloaded) =>
        new FlightMilestoneStore(_app).UpsertAsync(new FlightMilestones($"{flightNo}-20261022",
            departed is null ? null : At(departed), unloaded is null ? null : At(unloaded)));

    private async Task<List<(string Kind, string? CloseReason)>> AlertsOfAsync(string tag)
    {
        await using var cmd = fx.Db.CreateCommand(
            """
            SELECT a.kind, a.close_reason FROM bag_alerts a JOIN bags b ON b.bag_id = a.bag_id
            WHERE b.bag_tag_number = @t ORDER BY a.opened_at_utc, a.closed_at_utc NULLS LAST
            """);
        cmd.Parameters.AddWithValue("t", tag);
        var list = new List<(string, string?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        return list;
    }

    private static List<(string Kind, string? CloseReason)> L(params (string Kind, string? CloseReason)[] items) => [.. items];

    private async Task<long> NoticesOfAsync(string tag)
    {
        await using var cmd = fx.Db.CreateCommand("SELECT count(*) FROM outbox_messages WHERE topic = 'bag.alerts' AND partition_key = @t");
        cmd.Parameters.AddWithValue("t", tag);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Not_loaded_opens_after_the_grace_period_and_closes_when_the_bag_is_read()
    {
        var (missing, pnr) = await BagAsync("ZZ0801", ("SEC01", "063000"));
        var (loaded, _) = await BagAsync("ZZ0801", ("SEC01", "063000"), ("LOAD02", "074000"));
        await MilestonesAsync("ZZ0801", "080000", null);

        await Alerts().ScanAsync(At("080959"));
        Assert.Empty(await AlertsOfAsync(missing));

        await Alerts().ScanAsync(At("081000"));
        Assert.Equal(L((BagAlertKind.NotLoaded, null)), await AlertsOfAsync(missing));
        Assert.Empty(await AlertsOfAsync(loaded));

        var bag = Assert.Single((await Lookup(At("081000")).LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.Equal(BagAlertKind.NotLoaded, bag.Notice);
        Assert.Null(bag.Eta);

        // Okuma geç işlendi: bavul aslında yüklenmiş.
        await ScanAsync("ZZ0801", missing, "LOAD02", "075500");
        await Alerts().ScanAsync(At("081500"));

        Assert.Equal(L((BagAlertKind.NotLoaded, BagAlertCloseReason.Found)), await AlertsOfAsync(missing));
        Assert.Null(Assert.Single((await Lookup(At("081500")).LookupAsync(pnr, "YILMAZ"))!.Bags).Notice);
    }

    [Fact]
    public async Task Missing_at_arrival_opens_only_for_the_bag_that_was_not_unloaded()
    {
        var (missing, pnr) = await BagAsync("ZZ0802", ("LOAD02", "074000"));
        var (unloaded, _) = await BagAsync("ZZ0802", ("LOAD02", "074000"), ("UNLOAD", "121500"));
        await MilestonesAsync("ZZ0802", "080000", "122000");

        await Alerts().ScanAsync(At("122959"));
        Assert.Empty(await AlertsOfAsync(missing));

        await Alerts().ScanAsync(At("123000"));
        Assert.Equal(L((BagAlertKind.MissingAtArrival, null)), await AlertsOfAsync(missing));
        Assert.Empty(await AlertsOfAsync(unloaded));

        var flight = (await new OpsQueries(fx.Db, new FixedOperationalClock(At("123000"))).FlightsAsync()).Single(f => f.FlightId == "ZZ0802-20261022");
        Assert.Equal(1, flight.OpenAlerts);
        Assert.Contains(await new BagAlertQueries(_app).OpenAsync(), a => a.BagTag == missing && a.Kind == BagAlertKind.MissingAtArrival);
        Assert.Equal(BagAlertKind.MissingAtArrival, Assert.Single((await Lookup(At("123000")).LookupAsync(pnr, "YILMAZ"))!.Bags).Notice);

        // Bavul sonradan bant ağzında okundu (indirme okuması kaçırılmış).
        await ScanAsync("ZZ0802", missing, "CAROUSEL01", "123500");
        await Alerts().ScanAsync(At("123600"));
        Assert.Equal(L((BagAlertKind.MissingAtArrival, BagAlertCloseReason.Found)), await AlertsOfAsync(missing));
        Assert.DoesNotContain(await new BagAlertQueries(_app).OpenAsync(), a => a.BagTag == missing);
    }

    [Fact]
    public async Task Repeated_scans_do_not_open_the_same_alert_twice()
    {
        var (tag, _) = await BagAsync("ZZ0803", ("SEC01", "063000"));
        await MilestonesAsync("ZZ0803", "080000", null);

        await Alerts().ScanAsync(At("081000"));
        await Alerts().ScanAsync(At("081005"));
        await Alerts().ScanAsync(At("090000"));

        Assert.Single(await AlertsOfAsync(tag));
        Assert.Equal(1L, await NoticesOfAsync(tag));
    }

    [Fact]
    public async Task Never_loaded_bag_keeps_a_single_alert_through_arrival()
    {
        var (tag, _) = await BagAsync("ZZ0804", ("SEC01", "063000"));
        await MilestonesAsync("ZZ0804", "080000", "122000");

        await Alerts().ScanAsync(At("081000"));
        await Alerts().ScanAsync(At("123000"));

        Assert.Equal(L((BagAlertKind.NotLoaded, null)), await AlertsOfAsync(tag));
    }

    [Fact]
    public async Task Offloaded_bag_closes_its_alert_as_offloaded()
    {
        var (tag, _) = await BagAsync("ZZ0805", ("SEC01", "063000"));
        await MilestonesAsync("ZZ0805", "080000", null);
        await Alerts().ScanAsync(At("081000"));

        await using (var cmd = fx.Db.CreateCommand("UPDATE bags SET current_state = 'OFFLOADED_NO_SHOW' WHERE bag_tag_number = @t"))
        {
            cmd.Parameters.AddWithValue("t", tag);
            await cmd.ExecuteNonQueryAsync();
        }
        await Alerts().ScanAsync(At("081500"));

        Assert.Equal(L((BagAlertKind.NotLoaded, BagAlertCloseReason.Offloaded)), await AlertsOfAsync(tag));
    }

    [Fact]
    public async Task Closed_alert_cannot_be_changed_and_alerts_cannot_be_deleted()
    {
        var (tag, _) = await BagAsync("ZZ0806", ("SEC01", "063000"));
        await MilestonesAsync("ZZ0806", "080000", null);
        await Alerts().ScanAsync(At("081000"));
        await ScanAsync("ZZ0806", tag, "LOAD02", "075500");
        await Alerts().ScanAsync(At("081500"));

        await using var reopen = _app.CreateCommand(
            "UPDATE bag_alerts SET closed_at_utc = NULL, close_reason = NULL WHERE bag_id = (SELECT bag_id FROM bags WHERE bag_tag_number = @t)");
        reopen.Parameters.AddWithValue("t", tag);
        var ex1 = await Assert.ThrowsAsync<PostgresException>(() => reopen.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex1.SqlState);

        await using var delete = _app.CreateCommand("DELETE FROM bag_alerts");
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex2.SqlState);
    }

    [Fact]
    public async Task Late_unloading_start_is_shown_as_a_delay_notice_until_the_first_bag_is_unloaded()
    {
        var id = "ZZ0807-20261022";
        var (first, _) = await BagAsync("ZZ0807", ("LOAD02", "074000"));
        var (_, pnr) = await BagAsync("ZZ0807", ("LOAD02", "074000"));
        await new FlightContextStore(fx.Db).UpsertAsync(new FlightContext(id, At("122500"), false, false, 2, 500, 50, 0.5, 2, "HANDLER_A"));

        // İlk bavul hedefi (varsayılan 15 dk) on-block'tan sonra.
        Assert.Null(Assert.Single((await Lookup(At("123959")).LookupAsync(pnr, "YILMAZ"))!.Bags).Notice);
        Assert.Equal(BagNotice.ArrivalDelayed, Assert.Single((await Lookup(At("124000")).LookupAsync(pnr, "YILMAZ"))!.Bags).Notice);

        await ScanAsync("ZZ0807", first, "UNLOAD", "124100");
        Assert.Null(Assert.Single((await Lookup(At("124200")).LookupAsync(pnr, "YILMAZ"))!.Bags).Notice);
    }
}

/// <summary>W4: kilometre taşı ucu. API arka plan işleri çalıştığı için ayrı bir veritabanında (ayrı test sınıfı).</summary>
public sealed class FlightMilestoneEndpointTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);

    public void Dispose() => _factory.Dispose();

    private static string Json(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public async Task Milestones_are_signed_merged_and_validated()
    {
        var client = _factory.CreateClient();
        const string id = "ZZ0901-20261022";

        var unsigned = await client.PostAsync("/v1/flights/milestones",
            new StringContent(Json(new { flightId = id, departedUtc = "2026-10-22T08:00:00Z" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var d = await client.SendAsync(TestApi.Signed("/v1/flights/milestones",
            Json(new { flightId = id, departedUtc = "2026-10-22T08:00:00Z" }), "application/json"));
        var u = await client.SendAsync(TestApi.Signed("/v1/flights/milestones",
            Json(new { flightId = id, unloadingCompletedUtc = "2026-10-22T12:20:00Z" }), "application/json",
            sourceId: TestApi.ContextOnlyId, secret: TestApi.ContextOnlySecret));
        Assert.Equal(HttpStatusCode.Accepted, d.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, u.StatusCode);

        foreach (var bad in new object[]
                 {
                     new { flightId = id },
                     new { flightId = "zz 0901;", departedUtc = "2026-10-22T08:00:00Z" },
                     new { flightId = id, departedUtc = "2026-10-22T13:00:00Z", unloadingCompletedUtc = "2026-10-22T12:00:00Z" },
                 })
        {
            var resp = await client.SendAsync(TestApi.Signed("/v1/flights/milestones", Json(bad), "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        await using var cmd = fx.Db.CreateCommand("SELECT departed_utc, unloading_completed_utc FROM flight_milestones WHERE flight_id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero), r.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 12, 20, 0, TimeSpan.Zero), r.GetFieldValue<DateTimeOffset>(1));
    }
}
