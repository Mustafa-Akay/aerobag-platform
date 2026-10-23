using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AeroBag.Simulator;

public sealed record NoiseRates(double Duplicate, double Drop, double Corrupt);

/// <summary>
/// Mesajları sanal zaman sırasıyla API'ye gönderir. <paramref name="paceFromUtc"/> öncesindeki
/// mesajlar (kalkış öncesi olaylar) beklemeden gider; sonrası hız çarpanıyla (speed) zamanlanır.
/// Gürültü: kopya, kayıp ve bozuk mesaj. Sistemin dayanıklılığını göstermek içindir.
/// </summary>
public static class LiveSender
{
    public static async Task<SortedDictionary<string, int>> SendAsync(
        string apiBase,
        IReadOnlyList<ScheduledMessage> messages,
        DateTimeOffset paceFromUtc,
        double speed,
        NoiseRates noise,
        int seed,
        CancellationToken ct)
    {
        var counts = new SortedDictionary<string, int>();
        void Inc(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(10) };
        var rng = new SimRandom(seed);
        var clock = Stopwatch.StartNew();
        var sent = 0;

        foreach (var m in messages)
        {
            ct.ThrowIfCancellationRequested();

            if (m.TimeUtc > paceFromUtc)
            {
                var wait = (m.TimeUtc - paceFromUtc).TotalSeconds / speed - clock.Elapsed.TotalSeconds;
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
            }

            if (rng.Chance(noise.Drop))
            {
                Inc("DROPPED");
                continue;
            }

            var text = m.Text;
            if (rng.Chance(noise.Corrupt))
            {
                text = text[..rng.Int(5, Math.Max(6, text.Length - 5))];
                Inc("CORRUPTED_SENT");
            }

            await PostAsync(http, text, Inc, ct);
            if (rng.Chance(noise.Duplicate))
            {
                await PostAsync(http, text, Inc, ct);
                Inc("DUPLICATE_SENT");
            }

            sent++;
            if (sent % 200 == 0)
            {
                Console.WriteLine($"  {sent}/{messages.Count} mesaj işlendi...");
            }
        }

        return counts;
    }

    private static async Task PostAsync(HttpClient http, string text, Action<string> inc, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(text, Encoding.UTF8, "text/plain");
            using var response = await http.PostAsync("/v1/events", content, ct);
            switch ((int)response.StatusCode)
            {
                case 202:
                    inc("ACCEPTED");
                    var body = await response.Content.ReadAsStringAsync(ct);
                    using (var doc = JsonDocument.Parse(body))
                    {
                        if (doc.RootElement.TryGetProperty("outcome", out var outcome) && outcome.GetString() is { } o)
                        {
                            inc($"  outcome:{o}");
                        }
                    }
                    break;
                case 200:
                    inc("DUPLICATE");
                    break;
                case 422:
                    inc("REJECTED");
                    break;
                default:
                    inc($"HTTP_{(int)response.StatusCode}");
                    break;
            }
        }
        catch (HttpRequestException)
        {
            inc("CONNECTION_ERROR");
        }
    }
}
