using System.Text.Json;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace AeroBag.Api;

/// <summary>
/// Outbox mesajını içeriksiz SignalR bildirimlerine çevirir. Bavul grubu (etiket, uçuş) çiftiyle adlandırılır
/// (bkz. <see cref="LiveHub.BagGroup"/>); uçuşu olmayan bir yük hiçbir bavul grubuna gitmez.
/// </summary>
public sealed class SignalROutboxPublisher(IHubContext<LiveHub> hub) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.PayloadJson);
        var root = doc.RootElement;

        var flightId = root.TryGetProperty("flightId", out var flight) && flight.ValueKind == JsonValueKind.String
            ? flight.GetString()
            : null;

        if (flightId is not null && root.TryGetProperty("bagTag", out var tag) && tag.ValueKind == JsonValueKind.String
            && tag.GetString() is { } bagTag)
        {
            await hub.Clients.Group(LiveHub.BagGroup(bagTag, flightId)).SendAsync("bagChanged", new { bagTag }, ct);
        }

        // Uçuş düzeyindeki değişiklik (örn. bant ataması): o uçuştaki her bavulun grubuna ayrı bildirim.
        if (flightId is not null && root.TryGetProperty("bagTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { } each)
                {
                    await hub.Clients.Group(LiveHub.BagGroup(each, flightId)).SendAsync("bagChanged", new { bagTag = each }, ct);
                }
            }
        }

        if (flightId is not null)
        {
            await hub.Clients.Group(LiveHub.OpsGroup).SendAsync("flightsChanged", new { flightId }, ct);
        }
    }
}
