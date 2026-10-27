using System.Text.Json;

namespace AeroBag.Simulator;

/// <summary>Gerçek kurulumda AODB'nin yaptığı işi taklit eder: uçuş bağlamını (ML özellikleri) API'ye gönderir.</summary>
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
}
