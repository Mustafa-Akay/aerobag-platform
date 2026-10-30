using System.Collections.Concurrent;
using System.Text;
using AeroBag.Application.Security;

namespace AeroBag.Api;

/// <summary>Mesaj alma uçlarına verilen izin. Bir kaynak yalnızca kendi kapsamlarındaki uçlara yazabilir.</summary>
public static class IngestScopes
{
    public const string Events = "events";
    public const string Context = "context";
    public const string Gate = "gate";

    public static readonly string[] All = [Events, Context, Gate];
}

/// <summary>Uç üzerinde: bu uç imzalı istek ister ve kaynağın bu kapsama izni olmalıdır.</summary>
public sealed class RequireSourceScope(string scope)
{
    public string Scope { get; } = scope;
}

public sealed record IngestSource(string Id, byte[] Secret, IReadOnlySet<string> Scopes);

public sealed class IngestSettings
{
    public const int MinSecretBytes = 32;
    public const int MaxBodyBytes = 64 * 1024;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public required IReadOnlyDictionary<string, IngestSource> Sources { get; init; }
    public required bool UsingDemoSource { get; init; }

    public static IngestSettings FromConfiguration(IConfiguration config)
    {
        var list = new List<IngestSource>();
        foreach (var s in config.GetSection("Ingest:Sources").GetChildren())
        {
            var id = s["Id"];
            var secret = Encoding.UTF8.GetBytes(s["Secret"] ?? "");
            var scopes = (s["Scopes"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            if (!RequestSignature.IsValidSourceId(id))
            {
                throw new InvalidOperationException("Ingest:Sources içinde geçersiz kaynak adı (küçük harf, rakam, tire; en çok 32).");
            }
            if (secret.Length < MinSecretBytes)
            {
                throw new InvalidOperationException($"Ingest:Sources '{id}' gizli anahtarı en az {MinSecretBytes} bayt olmalı.");
            }
            if (scopes.Count == 0 || scopes.Any(x => !IngestScopes.All.Contains(x)))
            {
                throw new InvalidOperationException($"Ingest:Sources '{id}' kapsamları geçersiz (events, context, gate).");
            }
            list.Add(new IngestSource(id!, secret, scopes));
        }

        var demo = list.Count == 0;
        if (demo)
        {
            list.Add(new IngestSource(RequestSignature.DemoSourceId, Encoding.UTF8.GetBytes(RequestSignature.DemoSourceSecret),
                new HashSet<string>(IngestScopes.All, StringComparer.Ordinal)));
        }

        return new IngestSettings { Sources = list.ToDictionary(x => x.Id, StringComparer.Ordinal), UsingDemoSource = demo };
    }
}

public enum VerifyStatus { Ok, Unauthorized, Forbidden, TooLarge }

public sealed record VerifyResult(VerifyStatus Status, string? SourceId = null);

/// <summary>
/// İmzalı isteği doğrular: kaynak, kapsam, zaman damgası (±5 dk, gerçek saat), nonce, gövde özeti, HMAC.
/// Nonce yalnızca imza doğrulandıktan sonra kaydedilir; aynı nonce pencere içinde ikinci kez kabul edilmez.
/// Nonce önbelleği bellektedir ve tek örnek varsayar.
/// </summary>
public sealed class SourceVerifier(IngestSettings settings, TimeProvider clock)
{
    public const string SourceItemKey = "aerobag.source";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Son temizliğin zamanı (tick). Temizlik en fazla <see cref="SweepInterval"/>'da bir, tek iş parçacığında yapılır.</summary>
    private long _lastSweepTicks;

    /// <summary>
    /// W7 yük testinden önce önbellek 50.000 kaydı aşınca temizlik her istekte yapılıyordu: kayıtlar 10 dk geçerli olduğu için
    /// yoğun akışta (≥ ~83 istek/sn) hiçbiri silinmiyor, her istek bütün önbelleği dolaşıyordu (istek başına O(n)).
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    public async Task<VerifyResult> VerifyAsync(HttpContext ctx, string scope)
    {
        var req = ctx.Request;
        var sourceId = req.Headers[RequestSignature.SourceHeader].ToString();
        var tsText = req.Headers[RequestSignature.TimestampHeader].ToString();
        var nonce = req.Headers[RequestSignature.NonceHeader].ToString();
        var signature = req.Headers[RequestSignature.SignatureHeader].ToString();

        if (!RequestSignature.IsValidSourceId(sourceId) || !RequestSignature.IsValidNonce(nonce)
            || !long.TryParse(tsText, out var ts) || signature.Length is 0 or > 128
            || !settings.Sources.TryGetValue(sourceId, out var source))
        {
            return new VerifyResult(VerifyStatus.Unauthorized);
        }

        var now = clock.GetUtcNow();
        DateTimeOffset sentAt;
        try
        {
            sentAt = DateTimeOffset.FromUnixTimeSeconds(ts);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new VerifyResult(VerifyStatus.Unauthorized);
        }
        if ((now - sentAt).Duration() > IngestSettings.MaxClockSkew)
        {
            return new VerifyResult(VerifyStatus.Unauthorized);
        }

        var body = await ReadBodyAsync(req, ctx.RequestAborted);
        if (body is null)
        {
            return new VerifyResult(VerifyStatus.TooLarge);
        }

        var canonical = RequestSignature.Canonical(req.Method, req.Path.Value + req.QueryString.Value, ts, nonce, body);
        if (!RequestSignature.Verify(source.Secret, canonical, signature))
        {
            return new VerifyResult(VerifyStatus.Unauthorized);
        }

        if (!source.Scopes.Contains(scope))
        {
            return new VerifyResult(VerifyStatus.Forbidden, source.Id);
        }

        if (!Remember($"{source.Id}:{nonce}", now))
        {
            return new VerifyResult(VerifyStatus.Unauthorized);
        }

        return new VerifyResult(VerifyStatus.Ok, source.Id);
    }

    /// <summary>
    /// Nonce'u kaydeder; daha önce görüldüyse false. Süresi geçmiş kayıtlar (zaman penceresinin iki katı) en fazla 30 sn'de bir,
    /// tek bir istek tarafından atılır; diğer istekler beklemez. Pencere dışındaki istek zaten zaman damgası denetiminde
    /// reddedildiği için atılan nonce'un tekrar oynatılması mümkün değildir.
    /// </summary>
    private bool Remember(string key, DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last > SweepInterval.Ticks
            && Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) == last)
        {
            foreach (var (k, at) in _seen)
            {
                if (now - at > IngestSettings.MaxClockSkew * 2) _seen.TryRemove(k, out _);
            }
        }
        return _seen.TryAdd(key, now);
    }

    /// <summary>Gövdeyi okur ve sonraki okuyucu için başa sarar. Sınırı aşarsa null.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is > IngestSettings.MaxBodyBytes)
        {
            return null;
        }

        req.EnableBuffering(IngestSettings.MaxBodyBytes);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await req.Body.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > IngestSettings.MaxBodyBytes)
            {
                return null;
            }
        }
        req.Body.Position = 0;
        return ms.ToArray();
    }
}

public static class SourceSignatureMiddleware
{
    /// <summary>Uçta <see cref="RequireSourceScope"/> varsa imzayı doğrular; doğrulanan kaynak adını HttpContext.Items'a yazar.</summary>
    public static IApplicationBuilder UseSourceSignatures(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var required = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireSourceScope>();
        if (required is null)
        {
            await next(ctx);
            return;
        }

        var verifier = ctx.RequestServices.GetRequiredService<SourceVerifier>();
        var result = await verifier.VerifyAsync(ctx, required.Scope);
        switch (result.Status)
        {
            case VerifyStatus.Ok:
                ctx.Items[SourceVerifier.SourceItemKey] = result.SourceId;
                await next(ctx);
                return;
            case VerifyStatus.Forbidden:
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "Bu kaynağın bu uca yazma izni yok." });
                return;
            case VerifyStatus.TooLarge:
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await ctx.Response.WriteAsJsonAsync(new { error = "İstek gövdesi çok büyük." });
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "İmza doğrulanamadı." });
                return;
        }
    });

    public static string VerifiedSource(this HttpContext ctx) =>
        ctx.Items[SourceVerifier.SourceItemKey] as string ?? throw new InvalidOperationException("İmza doğrulaması çalışmadı.");
}
