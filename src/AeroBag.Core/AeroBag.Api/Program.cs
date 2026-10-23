using System.Text.RegularExpressions;
using AeroBag.Application.TypeB;
using AeroBag.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5433;Database=aerobag;Username=aerobag;Password=aerobag_dev";

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BaggageEventProcessor>();
builder.Services.AddSingleton<BagQueries>();

var app = builder.Build();

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

app.Run();

public partial class Program;
