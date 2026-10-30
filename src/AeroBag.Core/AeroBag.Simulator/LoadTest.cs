using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace AeroBag.Simulator;

/// <summary>
/// Yük testi (W7): imzalı Type B mesajlarını API'ye eşzamanlı gönderir ve her eşzamanlılık düzeyi için saniyedeki mesaj
/// sayısını, gecikme yüzdeliklerini ve hata oranını ölçer. Her bavulun mesajları sırayla gider (gerçek akışta da öyledir);
/// eşzamanlılık bavullar arasındadır. Veriler rastgele etiket ve uçuş numarasıyla üretilir, mevcut veriyle çakışmaz;
/// yine de ölçüm temiz bir veritabanında yapılmalıdır (operasyon saati ve panolar bu veriyi görür).
/// </summary>
public static class LoadTest
{
    public sealed record Level(int Concurrency, int Messages, TimeSpan Elapsed, IReadOnlyList<double> LatencyMs,
        IReadOnlyDictionary<string, int> Outcomes)
    {
        public double PerSecond => Messages / Elapsed.TotalSeconds;
        public int Errors => Outcomes.Where(kv => kv.Key is not ("202" or "200")).Sum(kv => kv.Value);
    }

    private static readonly string[] Points = ["SEC01", "LOAD02", "BUM", "CHUTE04", "CAROUSEL04", "CLAIM04"];

    /// <summary>Bir bavulun yaşam döngüsü: BSM, güvenlik, yükleme, indirme, şut, bant, banttan alma (7 mesaj).</summary>
    public static List<string> Lifecycle(Random rng, DateOnly date, DateTimeOffset start)
    {
        var flight = "LT" + rng.Next(1000, 10000).ToString(CultureInfo.InvariantCulture);
        var tag = rng.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        var pnr = new string(Enumerable.Range(0, 6).Select(_ => FlightGenerator.PnrChars[rng.Next(FlightGenerator.PnrChars.Length)]).ToArray());
        var list = new List<string> { MessageFormatter.Bsm(flight, date, tag, 18, "YUK", "TEST", pnr) };
        var t = start;
        foreach (var p in Points)
        {
            t = t.AddMinutes(1);
            list.Add(p == "BUM" ? MessageFormatter.Bum(flight, date, tag, "STAND101", t) : MessageFormatter.Bpm(flight, date, tag, p, t));
        }
        return list;
    }

    public static async Task<Level> RunAsync(string api, SourceCredentials creds, int concurrency, int messages, int seed, CancellationToken ct)
    {
        var rng = new Random(seed);
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var bags = Enumerable.Range(0, Math.Max(1, messages / 7))
            .Select(_ => Lifecycle(rng, date, dayStart.AddMinutes(rng.Next(0, 600))))
            .ToList();

        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency * 2, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(30) };

        var latencies = new ConcurrentBag<double>();
        var outcomes = new ConcurrentDictionary<string, int>();
        var next = -1;
        var clock = Stopwatch.StartNew();

        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= bags.Count) return;
                foreach (var text in bags[i])
                {
                    ct.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    string key;
                    try
                    {
                        using var req = creds.SignedPost("/v1/events", Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");
                        using var resp = await http.SendAsync(req, ct);
                        key = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (HttpRequestException)
                    {
                        key = "CONNECTION_ERROR";
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        key = "TIMEOUT";
                    }
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    outcomes.AddOrUpdate(key, 1, (_, n) => n + 1);
                }
            }
        }

        // Ara rapor (10 sn'de bir): hız zamanla düşüyorsa (ör. bir önbellek büyüdükçe) uzun koşuda görünür.
        using var progressCts = new CancellationTokenSource();
        var progress = Task.Run(async () =>
        {
            var last = 0;
            while (!progressCts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), progressCts.Token); }
                catch (OperationCanceledException) { return; }
                var done = latencies.Count;
                Console.WriteLine($"    {clock.Elapsed.TotalSeconds,5:0} sn: {done} mesaj, son 10 sn {(done - last) / 10.0:0.0} mesaj/sn");
                last = done;
            }
        });

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        clock.Stop();
        await progressCts.CancelAsync();
        await progress;
        return new Level(concurrency, latencies.Count, clock.Elapsed, latencies.ToList(), outcomes);
    }

    public static string Report(IReadOnlyList<Level> levels)
    {
        var inv = CultureInfo.InvariantCulture;
        string P(Level l, double p) => AlertEvaluation.Percentile(l.LatencyMs, p).ToString("0.0", inv);
        var sb = new StringBuilder();
        sb.AppendLine("Eşzamanlılık | mesaj | süre (sn) | mesaj/sn | saatte mesaj | gecikme p50 / p95 / p99 (ms) | en uzun (ms) | hata | yanıtlar");
        foreach (var l in levels)
        {
            sb.AppendLine(string.Format(inv, "{0} | {1} | {2:0.0} | {3:0.0} | {4:N0} | {5} / {6} / {7} | {8:0} | {9} | {10}",
                l.Concurrency, l.Messages, l.Elapsed.TotalSeconds, l.PerSecond, l.PerSecond * 3600,
                P(l, 0.5), P(l, 0.95), P(l, 0.99), l.LatencyMs.Count == 0 ? 0 : l.LatencyMs.Max(), l.Errors,
                string.Join(", ", l.Outcomes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"))));
        }
        return sb.ToString();
    }
}
