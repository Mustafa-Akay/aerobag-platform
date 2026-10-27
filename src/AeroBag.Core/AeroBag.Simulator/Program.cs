using System.Globalization;
using AeroBag.Simulator;

var a = ParseArgs(args);
if (a.ContainsKey("help"))
{
    Console.WriteLine("""
        AeroBag simülatörü

        Kullanım: dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- [seçenekler]

          --mode live|csv        live: API'ye gönderir, csv: veri seti yazar (varsayılan csv)
          --flights N            uçuş sayısı (varsayılan 3)
          --bags N               temel bavul sayısı, uçuş başına değişir (varsayılan 120)
          --seed N               tohum, aynı tohum aynı veriyi üretir (varsayılan 42)
          --regime A|B|M         A normal, B yağmurlu ve yavaş, M karışık (varsayılan M)
          --start ISO-TARİH      ilk uçuşun iniş saati (varsayılan bugün 12:00 UTC)
          csv modu:  --out YOL   (varsayılan data\synthetic\bags.csv)
          live modu: --api URL   (varsayılan http://localhost:5080)
                     --exit-scans false   çıkış kapısı taramalarını kapatır (varsayılan açık)
                     --speed X   sanal saniye / gerçek saniye (varsayılan 60)
                     --dup-rate P --drop-rate P --corrupt-rate P   (varsayılan 0.02, 0.01, 0.005)
                     --source-id AD          imzalı istek için kaynak adı (varsayılan simulator)
                     --source-secret ANAHTAR kaynağın gizli anahtarı (varsayılan: AEROBAG_SOURCE_SECRET ortam
                                             değişkeni, o da yoksa yalnızca yerel demo için olan varsayılan anahtar)
        """);
    return 0;
}

var mode = Get("mode", "csv").ToLowerInvariant();
var start = a.TryGetValue("start", out var s)
    ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
    : new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero);
var regime = char.ToUpperInvariant(Get("regime", "M")[0]);
if (regime is not ('A' or 'B' or 'M'))
{
    Console.Error.WriteLine("--regime A, B ya da M olmalı.");
    return 1;
}

var exitScans = mode == "live" && !string.Equals(Get("exit-scans", "true"), "false", StringComparison.OrdinalIgnoreCase);
var options = new SimOptions(GetInt("flights", 3), GetInt("bags", 120), GetInt("seed", 42), regime, start, exitScans);
var flights = SimulationRunner.Run(options);
var totalBags = flights.Sum(f => f.Bags.Count);
Console.WriteLine($"{flights.Count} uçuş, {totalBags} bavul üretildi (tohum {options.Seed}, rejim {regime}).");
foreach (var f in flights)
{
    var first = f.Bags.Single(b => b.IsFirst);
    var last = f.Bags.Single(b => b.IsLast);
    Console.WriteLine(
        $"  {f.Info.FlightId} rejim {f.Info.Regime} {f.Info.AircraftType} {f.Info.TotalBags} bavul bant {f.Info.Carousel}: " +
        $"ilk bavul {(first.CarouselUtc - f.Info.OnBlockUtc).TotalMinutes:F1} dk, son bavul {(last.CarouselUtc - f.Info.OnBlockUtc).TotalMinutes:F1} dk (iniş sonrası)");
}

Console.WriteLine("Örnek yolcular (yolcu sayfasında PNR + soyad ile sorgulanır):");
foreach (var f in flights)
{
    foreach (var b in f.Bags.Take(2))
    {
        Console.WriteLine($"  PNR {b.Pnr}  soyad {b.Surname}  (bavul {b.BagTag}, uçuş {b.FlightId})");
    }
}

if (mode == "csv")
{
    var path = Get("out", Path.Combine("data", "synthetic", "bags.csv"));
    CsvExporter.Write(path, flights);
    Console.WriteLine($"Veri seti yazıldı: {Path.GetFullPath(path)}");
    return 0;
}

if (mode == "live")
{
    var api = Get("api", "http://localhost:5080");
    var speed = GetDouble("speed", 60);
    var noise = new NoiseRates(GetDouble("dup-rate", 0.02), GetDouble("drop-rate", 0.01), GetDouble("corrupt-rate", 0.005));
    var secret = Get("source-secret", Environment.GetEnvironmentVariable("AEROBAG_SOURCE_SECRET") ?? SourceCredentials.Demo.Secret);
    var creds = new SourceCredentials(Get("source-id", SourceCredentials.Demo.Id), secret);
    var contexts = await ContextSender.SendAsync(api, flights.Select(f => f.Info), creds, CancellationToken.None);
    Console.WriteLine($"{contexts}/{flights.Count} uçuş bağlamı (AODB) gönderildi.");
    var messages = flights.SelectMany(f => f.Messages).OrderBy(m => m.TimeUtc).ToList();
    var paceFrom = flights.Min(f => f.Info.OnBlockUtc);

    Console.WriteLine($"{messages.Count} mesaj {api} adresine gönderiliyor (hız x{speed}, kopya/kayıp/bozuk oranları {noise.Duplicate}/{noise.Drop}/{noise.Corrupt})...");
    var counts = await LiveSender.SendAsync(api, messages, paceFrom, speed, noise, options.Seed + 1, creds, CancellationToken.None);
    Console.WriteLine("Sonuç:");
    foreach (var (key, value) in counts)
    {
        Console.WriteLine($"  {key}: {value}");
    }
    return counts.ContainsKey("CONNECTION_ERROR") ? 2 : 0;
}

Console.Error.WriteLine("--mode live ya da csv olmalı.");
return 1;

Dictionary<string, string> ParseArgs(string[] argv)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }
        var key = argv[i][2..];
        d[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? argv[++i] : "true";
    }
    return d;
}

string Get(string key, string fallback) => a.TryGetValue(key, out var v) ? v : fallback;

int GetInt(string key, int fallback) =>
    a.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

double GetDouble(string key, double fallback) =>
    a.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
