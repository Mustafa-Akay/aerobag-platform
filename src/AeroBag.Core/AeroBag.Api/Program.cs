using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using AeroBag.Api;
using AeroBag.Application.Ml;
using AeroBag.Application.TypeB;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5433;Database=aerobag;Username=aerobag_app;Password=aerobag_app_dev"; // kısıtlı uygulama rolü (db/init.sql)

// Yalnızca geliştirme varsayılanı; gerçek kurulumda ortam değişkeniyle (Security__LookupKeySecret) verilir.
var lookupSecret = builder.Configuration["Security:LookupKeySecret"] ?? "dev-only-secret-change-me-0123456789";

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new LookupKeyHasher(Encoding.UTF8.GetBytes(lookupSecret)));
builder.Services.AddSingleton<BaggageEventProcessor>();
builder.Services.AddSingleton<BagQueries>();
builder.Services.AddSingleton<EtaStatistics>();
builder.Services.AddSingleton<FlightContextStore>();
builder.Services.AddSingleton<PassengerQueries>();
builder.Services.AddSingleton<OpsQueries>();
builder.Services.AddSingleton<SlaQueries>();

// ExitGuard (Faz 4): çıkış kapısında bavul-PNR eşleştirmesi. Özellik bayrağı: ExitGuard:Enabled (varsayılan açık).
var exitGuard = new ExitGuardOptions(
    builder.Configuration.GetValue("ExitGuard:Enabled", true),
    builder.Configuration.GetValue("ExitGuard:MinSignalQuality", AeroBag.Application.ExitGuard.ExitGuardRules.DefaultMinSignalQuality));
builder.Services.AddSingleton(exitGuard);
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<SecurityReviews>();

// ML tahmin servisi (Faz 2). Ml:BaseUrl boşsa kapalıdır; servis erişilemezse sistem taban çizgisine düşer.
var mlUrl = builder.Configuration["Ml:BaseUrl"] ?? "http://localhost:8000";
if (string.IsNullOrWhiteSpace(mlUrl))
{
    builder.Services.AddSingleton<IMlPredictor, NullMlPredictor>();
}
else
{
    builder.Services.AddSingleton<IMlPredictor>(sp => new MlHttpPredictor(
        new HttpClient { BaseAddress = new Uri(mlUrl), Timeout = TimeSpan.FromSeconds(2) },
        sp.GetRequiredService<TimeProvider>()));
}
builder.Services.AddHostedService<MlOutcomeBackgroundService>();
builder.Services.AddSingleton<ExitDeadlineService>();
builder.Services.AddHostedService<ExitDeadlineBackgroundService>();

// Outbox -> SignalR: veritabanına yazılan olaylar arka planda içeriksiz canlı bildirimlere çevrilir.
builder.Services.AddSingleton<OutboxRelay>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IOutboxPublisher, SignalROutboxPublisher>();
builder.Services.AddHostedService<OutboxBackgroundService>();

// Kimlik doğrulama (Faz 4): kısa ömürlü HS256 JWT. Personel /v1/auth/token ile, yolcu /v1/lookup ile jeton alır.
// SignalR WebSocket bağlantısı başlık gönderemediği için /hubs altında jeton access_token sorgu parametresiyle de kabul edilir.
var auth = AuthSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton<TokenService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = auth.ValidationParameters();
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Roles.StaffPolicy, p => p.RequireRole(Roles.Ops, Roles.Security));
    o.AddPolicy(Roles.SecurityPolicy, p => p.RequireRole(Roles.Security));
});

// Mesaj kaynakları (Faz 4.2): /v1/events ve /v1/flights/context yalnızca HMAC imzalı istek kabul eder.
var ingest = IngestSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(ingest);
builder.Services.AddSingleton<SourceVerifier>();

// Yolcu sorgusu PNR denemesine açık olduğu için IP başına dakikada 20 istekle sınırlı.
// Personel girişi parola denemesine karşı IP başına dakikada 10 istekle sınırlı.
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("lookup", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();
if (auth.SigningKeyIsEphemeral)
{
    app.Logger.LogWarning("Auth:SigningKey verilmedi; bu açılış için rastgele bir imza anahtarı üretildi. Yeniden başlatınca jetonlar geçersiz olur.");
}
if (auth.UsingDemoUsers)
{
    app.Logger.LogWarning("Auth:Users verilmedi; demo kullanıcıları (ops, security) etkin. Yalnızca yerel demo içindir.");
}

if (ingest.UsingDemoSource)
{
    app.Logger.LogWarning("Ingest:Sources verilmedi; demo kaynağı (simulator) etkin. Yalnızca yerel demo içindir.");
}

app.UseAuthentication();
app.UseAuthorization();
app.UseSourceSignatures();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Ham (basitleştirilmiş) Type B metni: Content-Type text/plain, gövde en fazla MaxTextLength karakter.
app.MapPost("/v1/events", async (HttpRequest request, BaggageEventProcessor processor, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var buffer = new char[TypeBParser.MaxTextLength + 1];
    var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
    var text = new string(buffer, 0, count);

    var result = await processor.ProcessAsync(text, request.HttpContext.VerifiedSource(), ct);
    return result.Status switch
    {
        ProcessResult.Rejected => Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity),
        ProcessResult.Duplicate => Results.Json(result, statusCode: StatusCodes.Status200OK),
        _ => Results.Json(result, statusCode: StatusCodes.Status202Accepted),
    };
}).WithMetadata(new RequireSourceScope(IngestScopes.Events));

// Personel tüm bagajları, yolcu yalnızca jetonundaki bagajları görür. Yetkisiz bagaj için 404 döner (varlığı sızdırılmaz).
app.MapGet("/v1/bags/{bagTag}", async (string bagTag, string? flight, ClaimsPrincipal user, BagQueries queries, CancellationToken ct) =>
{
    if (!Regex.IsMatch(bagTag, @"^\d{10}$"))
    {
        return Results.BadRequest(new { error = "Bagaj etiketi 10 haneli olmalı." });
    }

    if (!user.CanSeeBag(bagTag))
    {
        return Results.NotFound();
    }

    var view = await queries.GetAsync(bagTag, flight, ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization();

// Personel girişi. Yanlış kullanıcı adı ve yanlış parola aynı yanıtı verir.
app.MapPost("/v1/auth/token", (LoginRequest body, TokenService tokens) =>
{
    var issued = tokens.IssueStaff(body.Username, body.Password);
    if (issued is null)
    {
        return Results.Json(new { error = "Kullanıcı adı ya da parola hatalı." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var role = auth.Users[body.Username!].Role;
    return Results.Ok(new { accessToken = issued.AccessToken, expiresInSec = issued.ExpiresInSec, role });
}).RequireRateLimiting("login");

// Yolcu sorgusu: PNR + soyad. Kayıt yok, PNR yanlış ve soyad yanlış durumları aynı yanıtı verir (bilgi sızdırmaz).
app.MapGet("/v1/lookup", async (string? pnr, string? surname, PassengerQueries queries, TokenService tokens, CancellationToken ct) =>
{
    var notFound = Results.NotFound(new { error = "Kayıt bulunamadı." });

    if (string.IsNullOrWhiteSpace(pnr) || string.IsNullOrWhiteSpace(surname) || pnr.Length > 8 || surname.Length > 64)
    {
        return notFound;
    }

    var result = await queries.LookupAsync(pnr, surname, ct);
    if (result is null)
    {
        return notFound;
    }

    // Yolcu jetonu yalnızca bu sorgudaki bagajlara erişim verir (canlı kanal ve /v1/bags için).
    var issued = tokens.IssuePassenger(result.Bags.Select(b => b.BagTag));
    return Results.Ok(new { asOfUtc = result.AsOfUtc, bags = result.Bags, accessToken = issued.AccessToken, expiresInSec = issued.ExpiresInSec });
}).RequireRateLimiting("lookup");

// Operasyon panosu: uçuş bazında sayaçlar. Yalnızca personel (ops, security).
app.MapGet("/v1/flights", async (OpsQueries ops, CancellationToken ct) => Results.Ok(await ops.FlightsAsync(ct)))
    .RequireAuthorization(Roles.StaffPolicy);

// SLA ve gecikme riski (DEMO hedefleri): ilk/son bavul için hedef, gerçekleşen ya da tahmin, ihlal olasılığı.
app.MapGet("/v1/flights/sla", async (SlaQueries sla, CancellationToken ct) => Results.Ok(await sla.GetAsync(ct)))
    .RequireAuthorization(Roles.StaffPolicy);

// AODB uçuş bağlamı beslemesi (ML tahmini için). Yalnızca 'context' kapsamlı, imzalı kaynaklar.
app.MapPost("/v1/flights/context", async (FlightContext body, FlightContextStore store, CancellationToken ct) =>
{
    var error = body.Validate();
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await store.UpsertAsync(body, ct);
    return Results.Accepted();
}).WithMetadata(new RequireSourceScope(IngestScopes.Context));

app.MapExitGuard(exitGuard);

app.MapHub<LiveHub>("/hubs/live");

app.Run();

public partial class Program;
