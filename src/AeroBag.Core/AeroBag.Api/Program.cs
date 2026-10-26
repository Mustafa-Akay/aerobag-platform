using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using AeroBag.Api;
using AeroBag.Application.Ml;
using AeroBag.Application.TypeB;
using AeroBag.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5433;Database=aerobag;Username=aerobag;Password=aerobag_dev";

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

// Yolcu sorgusu PNR denemesine açık olduğu için IP başına dakikada 20 istekle sınırlı.
builder.Services.AddRateLimiter(o =>
{
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
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Ham (basitleştirilmiş) Type B metni: Content-Type text/plain, gövde en fazla MaxTextLength karakter.
app.MapPost("/v1/events", async (HttpRequest request, BaggageEventProcessor processor, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var buffer = new char[TypeBParser.MaxTextLength + 1];
    var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
    var text = new string(buffer, 0, count);

    var result = await processor.ProcessAsync(text, "api", ct);
    return result.Status switch
    {
        ProcessResult.Rejected => Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity),
        ProcessResult.Duplicate => Results.Json(result, statusCode: StatusCodes.Status200OK),
        _ => Results.Json(result, statusCode: StatusCodes.Status202Accepted),
    };
});

app.MapGet("/v1/bags/{bagTag}", async (string bagTag, string? flight, BagQueries queries, CancellationToken ct) =>
{
    if (!Regex.IsMatch(bagTag, @"^\d{10}$"))
    {
        return Results.BadRequest(new { error = "Bagaj etiketi 10 haneli olmalı." });
    }

    var view = await queries.GetAsync(bagTag, flight, ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
});

// Yolcu sorgusu: PNR + soyad. Kayıt yok, PNR yanlış ve soyad yanlış durumları aynı yanıtı verir (bilgi sızdırmaz).
app.MapGet("/v1/lookup", async (string? pnr, string? surname, PassengerQueries queries, CancellationToken ct) =>
{
    var notFound = Results.NotFound(new { error = "Kayıt bulunamadı." });

    if (string.IsNullOrWhiteSpace(pnr) || string.IsNullOrWhiteSpace(surname) || pnr.Length > 8 || surname.Length > 64)
    {
        return notFound;
    }

    var result = await queries.LookupAsync(pnr, surname, ct);
    return result is null ? notFound : Results.Ok(result);
}).RequireRateLimiting("lookup");

// Operasyon panosu: uçuş bazında sayaçlar. (Kimlik doğrulama sonraki fazda eklenecek, şimdilik yalnızca yerel demo.)
app.MapGet("/v1/flights", async (OpsQueries ops, CancellationToken ct) => Results.Ok(await ops.FlightsAsync(ct)));

// SLA ve gecikme riski (DEMO hedefleri): ilk/son bavul için hedef, gerçekleşen ya da tahmin, ihlal olasılığı.
app.MapGet("/v1/flights/sla", async (SlaQueries sla, CancellationToken ct) => Results.Ok(await sla.GetAsync(ct)));

// AODB uçuş bağlamı beslemesi (ML tahmini için). Kimlik doğrulama sonraki fazda; şimdilik yalnızca yerel demo.
app.MapPost("/v1/flights/context", async (FlightContext body, FlightContextStore store, CancellationToken ct) =>
{
    var error = body.Validate();
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await store.UpsertAsync(body, ct);
    return Results.Accepted();
});

app.MapHub<LiveHub>("/hubs/live");

app.Run();

public partial class Program;
