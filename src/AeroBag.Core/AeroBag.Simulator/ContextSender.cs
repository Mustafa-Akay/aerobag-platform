using System.Globalization;
using System.Text.Json;

namespace AeroBag.Simulator;

/// <summary>Gerçek kurulumda AODB'nin yaptığı işi taklit eder: uçuş bağlamını (ML özellikleri ve bant ataması) API'ye gönderir.</summary>
public static class ContextSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> SendAsync(string apiBase, IEnumerable<FlightInfo> flights, SourceCredentials creds, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(10) };
        var ok = 0;
        foreach (var f in flights)
        {
            var body = new
            {
                flightId = f.FlightId,
                onBlockUtc = f.OnBlockUtc,
                wide = f.WideBody,
                rain = f.Weather == "RAIN",
                bags = f.TotalBags,
                standM = (double)f.StandDistanceM,
                chuteM = (double)f.ChuteToCarouselM,
                speedMps = f.ConveyorSpeedMps,
                arrivals30 = f.ArrivalsLast30Min,
                handler = f.GroundHandler,
                carousel = f.Carousel.ToString(CultureInfo.InvariantCulture),
            };
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
                using var req = creds.SignedPost("/v1/flights/context", json, "application/json; charset=utf-8");
                using var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode) ok++;
            }
            catch (HttpRequestException)
            {
                // API kapalıysa mesaj gönderimi de aynı hatayı raporlayacak.
            }
        }
        return ok;
    }

    /// <summary>
    /// AODB kilometre taşları (W4): kalkış ve varışta "indirme bitti". Gerçek AODB bunları olay anında gönderir; bu yüzden
    /// bavul mesajlarıyla aynı zaman çizelgesine konur (<see cref="LiveSender"/>, tür MILESTONE).
    /// </summary>
    /// <summary>
    /// Bağlantı uçuşlarının kilometre taşları (W10a): planlanan kalkış (ilk aktarma BSM'siyle birlikte) ve kalkış. Aynı bağlantı
    /// uçuşu birden çok gelen uçuşta görünür; tek kez, en erken bildirim anıyla gönderilir.
    /// </summary>
    public static IEnumerable<ScheduledMessage> OnwardMilestones(IEnumerable<OnwardFlight> onward)
    {
        foreach (var g in onward.GroupBy(o => o.FlightId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var o = g.First();
            var announced = g.Min(x => x.AnnouncedUtc);
            yield return new ScheduledMessage(announced,
                JsonSerializer.Serialize(new { flightId = o.FlightId, scheduledDepartureUtc = o.ScheduledDepartureUtc }, Json), "", "MILESTONE");
            yield return new ScheduledMessage(o.DepartedUtc,
                JsonSerializer.Serialize(new { flightId = o.FlightId, departedUtc = o.DepartedUtc }, Json), "", "MILESTONE");
        }
    }

    public static IEnumerable<ScheduledMessage> Milestones(FlightInfo f)
    {
        if (f.DepartedUtc is { } d)
        {
            yield return new ScheduledMessage(d, JsonSerializer.Serialize(new { flightId = f.FlightId, departedUtc = d }, Json), "", "MILESTONE");
        }
        if (f.UnloadingCompletedUtc is { } u)
        {
            yield return new ScheduledMessage(u, JsonSerializer.Serialize(new { flightId = f.FlightId, unloadingCompletedUtc = u }, Json), "", "MILESTONE");
        }
        // Yer hizmetinin uçuş düzeyinde bildirdiği ilk/son bavul bantta zamanları (W5); bant okuması yoksa SLA bunlardan.
        if (f.FirstBagOnBeltUtc is { } fb)
        {
            yield return new ScheduledMessage(fb, JsonSerializer.Serialize(new { flightId = f.FlightId, firstBagOnBeltUtc = fb }, Json), "", "MILESTONE");
        }
        if (f.LastBagOnBeltUtc is { } lb)
        {
            yield return new ScheduledMessage(lb, JsonSerializer.Serialize(new { flightId = f.FlightId, lastBagOnBeltUtc = lb }, Json), "", "MILESTONE");
        }
    }
}
