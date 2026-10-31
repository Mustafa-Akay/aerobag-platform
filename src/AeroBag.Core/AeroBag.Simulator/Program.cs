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
                     --exit-mode gate|bpm gate: ExitGuard kapı paketi (PNR'lı, varsayılan), bpm: eski BPM EXIT okuması
          gate-eval modu: ExitGuard kuralını API'siz, çok sayıda uçuşta değerlendirir (varsayılan --flights 200)
                     --thresholds 0.5,0.6,0.7,0.8   denenecek sinyal eşikleri
          alert-eval modu: kayıp bavul uyarısını API'siz, çok sayıda uçuşta değerlendirir (varsayılan --flights 200)
                     --graces 2,5,10,15,20   denenecek varış (indirme bitti) toleransları (dk); kalkış toleransı 10 dk
                     --drop-rate P --corrupt-rate P   okuma kaybı (varsayılan 0.01, 0.005)
          coverage-eval modu: okuma kapsamına duyarlılığı API'siz ölçer (varsayılan --flights 200); üç profil aynı gerçek
                     zaman çizelgesini görür. --drop-rate P --corrupt-rate P (varsayılan 0.01, 0.005)
          load modu: API'ye imzalı mesajları eşzamanlı gönderir, mesaj/sn ve gecikme yüzdeliklerini ölçer (W7).
                     --concurrency 1,8,32   denenecek eşzamanlılık düzeyleri   --count N   düzey başına mesaj (varsayılan 2100)
                     Temiz bir veritabanında çalıştırın (docker compose down -v, göç aracı).
          tüm modlar:  --coverage full|typical|minimal   okuma kapsamı (varsayılan full). typical: bant ağzı ve banttan alma
                       okuması yok; minimal: yalnızca yükleme ve indirme. Canlı modda API de aynı profille açılmalı
                       (Coverage__Profile ortam değişkeni).
          live ve alert-eval: --not-loaded-rate P   yüklenmeyen bavul oranı (varsayılan 0.003, demo varsayımı)
                              --missing-rate P      varışta bulunmayan bavul oranı (varsayılan 0.002, demo varsayımı)
          live: --transfer-rate P   aktarma bavulu oranı (varsayılan 0.06, demo varsayımı; diğer modlarda 0). Aktarma
                                    bavulu banda değil, 20 dk'lık tarifedeki bir bağlantı uçuşuna (ZZ5xxx) gider.
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

var exitScans = (mode == "live" && !string.Equals(Get("exit-scans", "true"), "false", StringComparison.OrdinalIgnoreCase)) || mode == "gate-eval";
var gatePackets = mode == "gate-eval" || !string.Equals(Get("exit-mode", "gate"), "bpm", StringComparison.OrdinalIgnoreCase);
// W4 senaryoları yalnızca canlı gösterimde ve uyarı ölçümünde varsayılan olarak açıktır; CSV (ML eğitimi) değişmez.
var scenarios = mode is "live" or "alert-eval";
var notLoadedRate = GetDouble("not-loaded-rate", scenarios ? 0.003 : 0);
var missingRate = GetDouble("missing-rate", scenarios ? 0.002 : 0);
if (notLoadedRate < 0 || missingRate < 0 || notLoadedRate + missingRate > 1)
{
    Console.Error.WriteLine("--not-loaded-rate ve --missing-rate 0 ile 1 arasında olmalı (toplamları en fazla 1).");
    return 1;
}
// W10a aktarma bavulları da yalnızca canlı gösterimde varsayılan olarak açık; ölçüm modları ve CSV (ML eğitimi) değişmez.
var transferRate = GetDouble("transfer-rate", mode == "live" ? 0.06 : 0);
if (transferRate is < 0 or > 0.5)
{
    Console.Error.WriteLine("--transfer-rate 0 ile 0.5 arasında olmalı.");
    return 1;
}
AeroBag.Application.Coverage.ReadCoverage coverage;
try
{
    coverage = AeroBag.Application.Coverage.ReadCoverage.Parse(Get("coverage", "full"));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message.Replace("Coverage:Profile", "--coverage", StringComparison.Ordinal));
    return 1;
}
var options = new SimOptions(GetInt("flights", mode is "gate-eval" or "alert-eval" or "coverage-eval" ? 200 : 3), GetInt("bags", 120), GetInt("seed", 42), regime, start,
    exitScans, exitScans && gatePackets, notLoadedRate, missingRate, coverage, transferRate);
var flights = SimulationRunner.Run(options);
var totalBags = flights.Sum(f => f.Bags.Count);
Console.WriteLine($"{flights.Count} uçuş, {totalBags} bavul üretildi (tohum {options.Seed}, rejim {regime}, okuma kapsamı {coverage.Name}).");
var transferBags = flights.SelectMany(f => f.Bags).Where(b => b.Transfer is not null).ToList();
if (transferBags.Count > 0)
{
    var onwardCount = flights.SelectMany(f => f.OnwardFlights).Select(x => x.FlightId).Distinct().Count();
    Console.WriteLine($"  Aktarma: {transferBags.Count} bavul, {onwardCount} bağlantı uçuşu; {transferBags.Count(b => b.Transfer!.MissedConnection)} bavul kabul kapanışına yetişmiyor.");
}
if (mode == "live" && coverage != AeroBag.Application.Coverage.ReadCoverage.Full)
{
    Console.WriteLine($"  Not: API de aynı kapsamla açılmalı: $env:Coverage__Profile=\"{coverage.Name}\" (yoksa bant okuması beklenir).");
}
foreach (var f in mode is "gate-eval" or "alert-eval" or "coverage-eval" or "load" ? Array.Empty<FlightResult>() : flights)
{
    var first = f.Bags.Single(b => b.IsFirst);
    var last = f.Bags.Single(b => b.IsLast);
    Console.WriteLine(
        $"  {f.Info.FlightId} rejim {f.Info.Regime} {f.Info.AircraftType} {f.Info.TotalBags} bavul bant {f.Info.Carousel}: " +
        $"ilk bavul {(first.CarouselUtc - f.Info.OnBlockUtc).TotalMinutes:F1} dk, son bavul {(last.CarouselUtc - f.Info.OnBlockUtc).TotalMinutes:F1} dk (iniş sonrası)");
}

if (mode == "gate-eval")
{
    var thresholds = Get("thresholds", "0.5,0.6,0.7,0.8").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
    var packets = flights.Sum(f => f.Messages.Count(m => m.Gate is not null));
    Console.WriteLine($"{packets} kapı paketi. Sensör hata oranları demo varsayımıdır (GateModel.cs).");
    Console.WriteLine(GateEvaluation.Report(thresholds.Select(t => GateEvaluation.Evaluate(flights, t)).ToList()));
    return 0;
}

if (mode == "load")
{
    var api = Get("api", "http://localhost:5080");
    var secret = Get("source-secret", Environment.GetEnvironmentVariable("AEROBAG_SOURCE_SECRET") ?? SourceCredentials.Demo.Secret);
    var creds = new SourceCredentials(Get("source-id", SourceCredentials.Demo.Id), secret);
    var levels = Get("concurrency", "1,8,32").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
    var count = GetInt("count", 2100);
    if (levels.Any(l => l is < 1 or > 256) || count < 7)
    {
        Console.Error.WriteLine("--concurrency 1-256 arası, --count en az 7 olmalı.");
        return 1;
    }

    Console.WriteLine($"Yük testi: {api}, düzey başına {count} mesaj, eşzamanlılık {string.Join("/", levels)}.");
    Console.WriteLine("Hedef (EVALUATION.md): yoğun bir havalimanının yoğun saati ~17 mesaj/sn; 5 kat pay ile 100 mesaj/sn.");
    // Isınma: JIT, bağlantı havuzu ve veritabanı önbelleği; sonuca katılmaz.
    // Tohuma zaman da katılır: her çalıştırma yeni bavullar üretir (aynı bavul tekrar gelirse API onu tekrar sayar ve ölçüm kirlenir).
    var runSeed = unchecked(options.Seed * 397 + (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000));
    await LoadTest.RunAsync(api, creds, 4, 140, runSeed + 999, CancellationToken.None);
    var results = new List<LoadTest.Level>();
    for (var i = 0; i < levels.Count; i++)
    {
        var r = await LoadTest.RunAsync(api, creds, levels[i], count, unchecked(runSeed + i * 7919), CancellationToken.None);
        results.Add(r);
        Console.WriteLine($"  eşzamanlılık {r.Concurrency}: {r.PerSecond:0.0} mesaj/sn, hata {r.Errors}");
    }
    Console.WriteLine();
    Console.WriteLine(LoadTest.Report(results));
    return results.Any(r => r.Outcomes.ContainsKey("CONNECTION_ERROR")) ? 2 : 0;
}

if (mode == "coverage-eval")
{
    var drop = GetDouble("drop-rate", 0.01);
    var corrupt = GetDouble("corrupt-rate", 0.005);
    var profiles = new[] { AeroBag.Application.Coverage.ReadCoverage.Full, AeroBag.Application.Coverage.ReadCoverage.Typical,
        AeroBag.Application.Coverage.ReadCoverage.Minimal };
    // Aynı tohum: üç profil aynı gerçek zaman çizelgesini görür, yalnızca okumalar değişir.
    var rows = profiles.Select(p => CoverageEvaluation.Evaluate(SimulationRunner.Run(options with { Coverage = p }), p, drop, corrupt, options.Seed + 1)).ToList();
    Console.WriteLine($"Okuma kaybı {drop}+{corrupt}. ML servisi bu ölçümde yok: indirme sonrası tahmin varsayılan aralıktır.");
    Console.WriteLine(CoverageEvaluation.Report(rows));
    Console.WriteLine("Kayıp bavul uyarısının kapsamaya duyarlılığı için: --mode alert-eval --coverage typical|minimal");
    return 0;
}

if (mode == "alert-eval")
{
    var graces = Get("graces", "2,5,10,15,20").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
    var drop = GetDouble("drop-rate", 0.01);
    var corrupt = GetDouble("corrupt-rate", 0.005);
    var nl = flights.Sum(f => f.Bags.Count(b => b.Fate == BagFate.NotLoaded));
    var mi = flights.Sum(f => f.Bags.Count(b => b.Fate == BagFate.MissingAtArrival));
    Console.WriteLine($"Senaryo: {nl} yüklenmeyen, {mi} varışta bulunmayan bavul. Okuma kaybı {drop}+{corrupt}. Oranlar demo varsayımıdır.");
    var rows = new List<AlertEvaluation.Row>
    {
        AlertEvaluation.Evaluate(flights, AeroBag.Application.Alerts.AlertPolicy.Default, 0, 0, options.Seed + 1, "kayıpsız", coverage),
    };
    // Kalkış toleransı varsayılanda sabit; denenen, varış ("indirme bitti") toleransıdır.
    rows.AddRange(graces.Select(g => AlertEvaluation.Evaluate(flights,
        AeroBag.Application.Alerts.AlertPolicy.Default with { MissingGrace = TimeSpan.FromMinutes(g) }, drop, corrupt, options.Seed + 1, "kayıplı", coverage)));
    Console.WriteLine(AlertEvaluation.Report(rows));
    return 0;
}

Console.WriteLine("Örnek yolcular (yolcu sayfasında PNR + soyad ile sorgulanır):");
foreach (var f in flights)
{
    foreach (var b in f.Bags.Take(2))
    {
        Console.WriteLine($"  PNR {b.Pnr}  soyad {b.Surname}  (bavul {b.BagTag}, uçuş {b.FlightId})");
    }
}

var scenarioBags = flights.SelectMany(f => f.Bags).Where(b => b.Fate is BagFate.NotLoaded or BagFate.MissingAtArrival).ToList();
if (mode == "live" && scenarioBags.Count > 0)
{
    Console.WriteLine("Senaryo bavulları (W4; yolcu sayfasında uyarıyı görmek için):");
    foreach (var b in scenarioBags)
    {
        var fate = b.Fate == BagFate.NotLoaded ? "uçağa yüklenmeyecek" : "varışta indirilmeyecek";
        Console.WriteLine($"  PNR {b.Pnr}  soyad {b.Surname}  (bavul {b.BagTag}, uçuş {b.FlightId}): {fate}");
    }
}

if (mode == "live" && transferBags.Count > 0)
{
    // Yetişenlerden ve yetişmeyenlerden birkaç örnek (W10a; yolcu sayfasında aktarma kartını görmek için).
    Console.WriteLine("Aktarma bavulları (örnek):");
    foreach (var b in transferBags.Where(b => !b.Transfer!.MissedConnection).Take(3).Concat(transferBags.Where(b => b.Transfer!.MissedConnection).Take(2)))
    {
        var t = b.Transfer!;
        var outcome = t.MissedConnection ? "kabul kapanışına yetişmeyecek" : "bağlantı uçağına yüklenecek";
        Console.WriteLine($"  PNR {b.Pnr}  soyad {b.Surname}  (bavul {b.BagTag}, {b.FlightId} -> {t.OnwardFlightId}): {outcome}");
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
    // Kilometre taşları (kalkış, indirme bitti) bavul okumalarıyla aynı zaman çizelgesinde, olay anında gider.
    var messages = flights.SelectMany(f => f.Messages).Concat(flights.SelectMany(f => ContextSender.Milestones(f.Info)))
        .Concat(ContextSender.OnwardMilestones(flights.SelectMany(f => f.OnwardFlights)))
        .OrderBy(m => m.TimeUtc).ToList();
    var paceFrom = flights.Min(f => f.Info.OnBlockUtc);

    Console.WriteLine($"{messages.Count} mesaj {api} adresine gönderiliyor (hız x{speed}, kopya/kayıp/bozuk oranları {noise.Duplicate}/{noise.Drop}/{noise.Corrupt})...");
    var counts = await LiveSender.SendAsync(api, messages, paceFrom, speed, noise, options.Seed + 1, creds, CancellationToken.None);
    Console.WriteLine("Sonuç:");
    foreach (var (key, value) in counts)
    {
        Console.WriteLine($"  {key}: {value}");
    }

    var legitReads = counts.Where(kv => kv.Key.StartsWith("GATE ", StringComparison.Ordinal) && !kv.Key.StartsWith("GATE WRONG_PERSON", StringComparison.Ordinal)
        && !kv.Key.StartsWith("GATE HTTP", StringComparison.Ordinal) && !kv.Key.EndsWith("DUPLICATE", StringComparison.Ordinal)).Sum(kv => kv.Value);
    if (legitReads > 0)
    {
        var falseAlarms = counts.Where(kv => kv.Key.StartsWith("GATE ", StringComparison.Ordinal) && !kv.Key.StartsWith("GATE WRONG_PERSON", StringComparison.Ordinal)
            && kv.Key.EndsWith("REVIEW_MISMATCH", StringComparison.Ordinal)).Sum(kv => kv.Value);
        Console.WriteLine($"ExitGuard (bu çalıştırma): {legitReads} meşru okumada {falseAlarms} yanlış alarm. Oranlar için --mode gate-eval kullanın.");
    }
    return counts.ContainsKey("CONNECTION_ERROR") ? 2 : 0;
}

Console.Error.WriteLine("--mode live, csv, gate-eval, alert-eval, coverage-eval ya da load olmalı.");
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
