using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AeroBag.Api;

/// <summary>
/// Canlı bildirim kanalı. Mesajlar içeriksizdir: "bu bagaj değişti" / "bu uçuş değişti" der, ayrıntıyı
/// istemci mevcut uçlardan (örn. /v1/lookup) yeniden çeker. Böylece kanal kişisel veri taşımaz.
/// Bağlanmak için jeton gerekir. Operasyon grubuna yalnızca personel, bir bagaj grubuna yalnızca personel ya da
/// jetonunda o (etiket, uçuş) çifti bulunan yolcu katılabilir. Gruplar "bag:etiket@uçuş" adlıdır: etiket numarası
/// başka bir gün yeniden kullanılsa bile o günün bildirimleri eski yolcuya gitmez.
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    public const string OpsGroup = "ops";
    private const int MaxBagsPerConnection = 20;

    public static string BagGroup(string bagTag, string flightId) => $"bag:{BagKey.Format(bagTag, flightId)}";

    public Task JoinOps()
    {
        if (Context.User?.IsStaff() != true)
        {
            throw new HubException("Bu kanal için yetkiniz yok.");
        }
        return Groups.AddToGroupAsync(Context.ConnectionId, OpsGroup);
    }

    /// <param name="bagKeys">"etiket@uçuş" biçiminde en çok 20 anahtar (bkz. <see cref="BagKey"/>).</param>
    public async Task JoinBags(string[] bagKeys)
    {
        if (bagKeys is null || bagKeys.Length > MaxBagsPerConnection)
        {
            throw new HubException("Geçersiz bagaj listesi.");
        }

        var keys = new List<BagKey>(bagKeys.Length);
        foreach (var raw in bagKeys)
        {
            if (!BagKey.TryParse(raw, out var key))
            {
                throw new HubException("Geçersiz bagaj listesi.");
            }
            keys.Add(key);
        }

        var user = Context.User;
        if (user is null || keys.Any(k => !user.CanSeeBag(k.BagTag, k.FlightId)))
        {
            throw new HubException("Bu bagaj için yetkiniz yok.");
        }

        foreach (var k in keys)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, BagGroup(k.BagTag, k.FlightId));
        }
    }
}
