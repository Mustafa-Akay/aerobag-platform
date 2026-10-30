using System.Net;
using System.Text;
using System.Text.Json;
using AeroBag.Application.Ml;
using AeroBag.Infrastructure;

namespace AeroBag.UnitTests;

/// <summary>
/// W6: MlHttpPredictor ile ML servisi arasındaki sözleşme (contracts/ml). İstemci, sahte bir HTTP işleyiciye karşı çalışır:
/// gönderdiği istek örnek istekle aynı alanları ve tipleri taşımalı, örnek yanıtı doğru okumalı. Python tarafı aynı dosyalara
/// karşı test edilir (ml/tests/test_contract.py). Ayrıca hata yolları: bekleme süresi, zaman aşımı, sözleşmeye uymayan yanıt.
/// </summary>
public class MlContractTests
{
    private static readonly FlightContext Ctx =
        new("ZZ0100-20261030", new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), false, true, 120, 1000, 50, 0.55, 2, "HANDLER_B");

    private static string Contract(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "ml", name));

    /// <summary>JSON'un alan adları ve tipleri (değerler değil). Tam sayı ile ondalık aynı ("number").</summary>
    private static string Shape(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + ":" + Shape(p.Value))) + "}",
        JsonValueKind.Array => "[" + (e.GetArrayLength() > 0 ? Shape(e[0]) : "") + "]",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    private static string Shape(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Shape(doc.RootElement);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (MlHttpPredictor Ml, FakeHandler Handler, MlStatus Status, FakeTime Time) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var time = new FakeTime(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var status = new MlStatus();
        var ml = new MlHttpPredictor(new HttpClient(handler) { BaseAddress = new Uri("http://ml.test") }, time, status);
        return (ml, handler, status, time);
    }

    [Fact]
    public async Task Flight_prediction_follows_the_contract()
    {
        var (ml, h, status, _) = Create(_ => Json(Contract("predict.response.json")));

        var f = await ml.PredictAsync(Ctx);

        Assert.Equal("/v1/predict", h.LastPath);
        Assert.Equal(Shape(Contract("predict.request.json")), Shape(h.LastBody!));
        Assert.NotNull(f);
        Assert.Equal(new MinuteQuantiles(9.8, 12.6, 17.9), f!.First);
        Assert.Equal(new MinuteQuantiles(4.1, 7.2, 12.4), f.Span);
        Assert.Equal(new MinuteQuantiles(15.7, 20.3, 28.1), f.Last);
        Assert.True(status.Snapshot(DateTimeOffset.MaxValue).Healthy);
    }

    [Fact]
    public async Task Bag_prediction_follows_the_contract()
    {
        var (ml, h, _, _) = Create(_ => Json(Contract("predict-bag.response.json")));

        var f = await ml.PredictBagAsync(Ctx);

        Assert.Equal("/v1/predict-bag", h.LastPath);
        Assert.Equal(Shape(Contract("predict-bag.request.json")), Shape(h.LastBody!));
        Assert.Equal(new BagDurationForecast(212.5, 318.0, 541.2), f);
    }

    [Fact]
    public async Task Bag_outcomes_follow_the_contract()
    {
        var (ml, h, _, _) = Create(_ => Json(Contract("outcome-bags.response.json")));

        var recorded = await ml.ReportBagOutcomesAsync([new BagOutcome(Ctx, 305)]);

        Assert.Equal("/v1/outcome-bags", h.LastPath);
        Assert.Equal(Shape(Contract("outcome-bags.request.json")), Shape(h.LastBody!));
        Assert.Equal(1, recorded);
    }

    [Fact]
    public async Task Flight_outcome_follows_the_contract()
    {
        var (ml, h, _, _) = Create(_ => Json(Contract("outcome.response.json")));

        var recorded = await ml.ReportOutcomeAsync(Ctx.FlightId, 11.4, 24.9);

        Assert.Equal("/v1/outcome", h.LastPath);
        Assert.Equal(Shape(Contract("outcome.request.json")), Shape(h.LastBody!));
        Assert.True(recorded);
    }

    [Fact]
    public async Task Server_error_backs_off_records_the_reason_and_recovers()
    {
        var fail = true;
        var (ml, h, status, time) = Create(_ => fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(Contract("predict.response.json")));

        Assert.Null(await ml.PredictAsync(Ctx));
        var s = status.Snapshot(time.Now);
        Assert.False(s.Healthy);
        Assert.Equal("HTTP 500", s.LastFailureReason);
        Assert.Equal(time.Now + MlHttpPredictor.FailureBackoff, s.BackoffUntilUtc);

        // Bekleme süresinde servise hiç gidilmez.
        Assert.Null(await ml.PredictBagAsync(Ctx));
        Assert.Equal(1, h.Calls);

        fail = false;
        time.Now += MlHttpPredictor.FailureBackoff + TimeSpan.FromSeconds(1);
        Assert.NotNull(await ml.PredictAsync(Ctx));
        Assert.Equal(2, h.Calls);
        Assert.True(status.Snapshot(time.Now).Healthy);
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_timeout()
    {
        var (ml, _, status, time) = Create(_ => throw new TaskCanceledException("HttpClient.Timeout"));

        Assert.Null(await ml.PredictBagAsync(Ctx));
        Assert.Equal("zaman aşımı", status.Snapshot(time.Now).LastFailureReason);
    }

    [Fact]
    public async Task Response_that_breaks_the_contract_is_not_trusted()
    {
        var renamed = Contract("predict.response.json").Replace("minutes_from_on_block", "minutes_from_onblock", StringComparison.Ordinal);
        var (ml, _, status, time) = Create(_ => Json(renamed));

        Assert.Null(await ml.PredictAsync(Ctx));
        Assert.Equal("sözleşmeye uymayan yanıt", status.Snapshot(time.Now).LastFailureReason);
    }

    [Fact]
    public async Task Short_outage_reuses_the_last_flight_forecast()
    {
        var fail = false;
        var (ml, _, _, time) = Create(_ => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Contract("predict.response.json")));
        var first = await ml.PredictAsync(Ctx);

        fail = true;
        time.Now += TimeSpan.FromSeconds(31); // önbellek süresi geçti, eski sonuç süresi (10 dk) geçmedi
        Assert.Equal(first, await ml.PredictAsync(Ctx));
    }

    [Fact]
    public async Task Feedback_failure_is_recorded_but_does_not_pause_predictions()
    {
        var (ml, h, status, time) = Create(r =>
            r.RequestUri!.AbsolutePath == "/v1/outcome" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(Contract("predict.response.json")));

        Assert.False(await ml.ReportOutcomeAsync(Ctx.FlightId, 11.4, 24.9));
        Assert.Equal("HTTP 502", status.Snapshot(time.Now).LastFailureReason);
        Assert.Null(status.Snapshot(time.Now).BackoffUntilUtc);

        Assert.NotNull(await ml.PredictAsync(Ctx));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public void Every_contract_file_is_present()
    {
        var names = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "contracts", "ml"), "*.json")
            .Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "health.response.json", "outcome-bags.request.json", "outcome-bags.response.json", "outcome.request.json", "outcome.response.json",
             "predict-bag.request.json", "predict-bag.response.json", "predict.request.json", "predict.response.json" },
            names);
    }
}
