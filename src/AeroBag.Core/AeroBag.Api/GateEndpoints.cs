using System.Security.Claims;
using System.Text.RegularExpressions;
using AeroBag.Infrastructure;

namespace AeroBag.Api;

public sealed record PassengerVerificationRequest(string? Method, string? PresentedPnr, string? PassengerSurname, double? SignalQuality);

public sealed record GateScanRequest(string? GateId, DateTimeOffset? TimestampUtc, string[]? DetectedBagTags, PassengerVerificationRequest? PassengerVerification);

public sealed record ResolveReviewRequest(string? Resolution, string? Note);

public static partial class GateEndpoints
{
    public const int MaxTagsPerScan = 10;

    /// <summary>Paketi doğrular. Soyad alanı kabul edilir ama kullanılmaz ve saklanmaz (eşleştirme PNR düzeyindedir).</summary>
    public static string? TryCreate(GateScanRequest body, out GateScan? scan)
    {
        scan = null;
        if (body.GateId is null || !GateIdPattern().IsMatch(body.GateId)) return "gateId geçersiz.";
        if (body.TimestampUtc is null) return "timestampUtc gerekli.";
        var tags = body.DetectedBagTags ?? [];
        if (tags.Length is 0 or > MaxTagsPerScan || tags.Any(t => t is null || !TagPattern().IsMatch(t))) return $"detectedBagTags 1-{MaxTagsPerScan} adet 10 haneli etiket olmalı.";

        var v = body.PassengerVerification;
        string? pnr = null;
        if (v is not null)
        {
            if (v.PresentedPnr is not null)
            {
                pnr = v.PresentedPnr.Trim().ToUpperInvariant();
                if (!PnrPattern().IsMatch(pnr)) return "presentedPnr geçersiz.";
            }
            if (v.SignalQuality is { } q && (double.IsNaN(q) || q < 0 || q > 1)) return "signalQuality 0-1 olmalı.";
            if (v.Method is not null && !MethodPattern().IsMatch(v.Method)) return "method geçersiz.";
            if (v.PassengerSurname is { Length: > 64 }) return "passengerSurname çok uzun.";
        }

        scan = new GateScan(body.GateId, body.TimestampUtc.Value.ToUniversalTime(), tags, v?.Method, pnr, v?.SignalQuality);
        return null;
    }

    public static void MapExitGuard(this WebApplication app, ExitGuardOptions options)
    {
        if (options.Enabled)
        {
            // Kapı sensörü: yalnızca 'gate' kapsamlı, imzalı kaynaklar. Kapıda alarm yok; sonuç cihaza yalnızca bilgi olarak döner.
            app.MapPost("/v1/gate/scans", async (GateScanRequest body, HttpContext http, GateService gate, CancellationToken ct) =>
            {
                var error = TryCreate(body, out var scan);
                if (error is not null)
                {
                    return Results.BadRequest(new { error });
                }
                var results = await gate.ProcessAsync(scan!, http.VerifiedSource(), ct);
                return Results.Ok(new { results });
            }).WithMetadata(new RequireSourceScope(IngestScopes.Gate));
        }

        // Güvenlik ekranı: yalnızca güvenlik rolü. Kayıtlarda kişisel veri yoktur.
        app.MapGet("/v1/security/reviews", async (string? status, SecurityReviews reviews, CancellationToken ct) =>
                Results.Ok(await reviews.ListAsync(openOnly: !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase), ct: ct)))
            .RequireAuthorization(Roles.SecurityPolicy);

        app.MapPost("/v1/security/reviews/{id:guid}/resolve", async (Guid id, ResolveReviewRequest body, ClaimsPrincipal user,
                SecurityReviews reviews, CancellationToken ct) =>
            {
                if (body.Resolution is null || !SecurityReviews.Resolutions.Contains(body.Resolution))
                {
                    return Results.BadRequest(new { error = "resolution CONFIRMED ya da FALSE_ALARM olmalı." });
                }
                if (body.Note is { Length: > 200 })
                {
                    return Results.BadRequest(new { error = "note en çok 200 karakter olmalı." });
                }

                var by = user.FindFirst(Roles.SubjectClaim)?.Value ?? "unknown";
                return await reviews.ResolveAsync(id, body.Resolution, body.Note, by, ct) switch
                {
                    ResolveOutcome.Resolved => Results.Ok(new { status = "RESOLVED" }),
                    ResolveOutcome.AlreadyResolved => Results.Conflict(new { error = "Bu kayıt zaten sonuçlandırılmış." }),
                    _ => Results.NotFound(),
                };
            })
            .RequireAuthorization(Roles.SecurityPolicy);
    }

    [GeneratedRegex("^[A-Z0-9_]{1,32}$")]
    private static partial Regex GateIdPattern();

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[A-Z0-9]{5,8}$")]
    private static partial Regex PnrPattern();

    [GeneratedRegex("^[A-Z_]{1,32}$")]
    private static partial Regex MethodPattern();
}
