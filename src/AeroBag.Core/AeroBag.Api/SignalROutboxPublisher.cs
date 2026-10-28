using System.Text.Json;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace AeroBag.Api;

/// <summary>Outbox mesajını içeriksiz SignalR bildirimlerine çevirir.</summary>
public sealed class SignalROutboxPublisher(IHubContext<LiveHub> hub) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.PayloadJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("bagTag", out var tag) && tag.GetString() is { } bagTag)
        {
            await hub.Clients.Group(LiveHub.BagGroup(bagTag)).SendAsync("bagChanged", new { bagTag }, ct);
        }

        // Uçuş düzeyindeki değişiklik (örn. bant ataması): o uçuştaki her bavulun grubuna ayrı bildirim.
        if (root.TryGetProperty("bagTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
            {
                if (t.GetString() is { } each)
                {
                    await hub.Clients.Group(LiveHub.BagGroup(each)).SendAsync("bagChanged", new { bagTag = each }, ct);
                }
            }
        }

        if (root.TryGetProperty("flightId", out var flight) && flight.GetString() is { } flightId)
        {
            await hub.Clients.Group(LiveHub.OpsGroup).SendAsync("flightsChanged", new { flightId }, ct);
        }
    }
}
