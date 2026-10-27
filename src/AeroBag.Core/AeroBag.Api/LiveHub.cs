using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AeroBag.Api;

/// <summary>
/// Canlı bildirim kanalı. Mesajlar içeriksizdir: "bu bagaj değişti" / "bu uçuş değişti" der, ayrıntıyı
/// istemci mevcut uçlardan (örn. /v1/lookup) yeniden çeker. Böylece kanal kişisel veri taşımaz.
/// Bağlanmak için jeton gerekir. Operasyon grubuna yalnızca personel, bir bagaj grubuna yalnızca personel ya da
/// jetonunda o bagaj bulunan yolcu katılabilir.
/// </summary>
[Authorize]
public sealed partial class LiveHub : Hub
{
    public const string OpsGroup = "ops";
    private const int MaxBagsPerConnection = 20;

    public static string BagGroup(string bagTag) => $"bag:{bagTag}";

    public Task JoinOps()
    {
        if (Context.User?.IsStaff() != true)
        {
            throw new HubException("Bu kanal için yetkiniz yok.");
        }
        return Groups.AddToGroupAsync(Context.ConnectionId, OpsGroup);
    }

    public async Task JoinBags(string[] bagTags)
    {
        if (bagTags is null || bagTags.Length > MaxBagsPerConnection || bagTags.Any(t => t is null || !BagTagPattern().IsMatch(t)))
        {
            throw new HubException("Geçersiz bagaj listesi.");
        }

        var user = Context.User;
        if (user is null || bagTags.Any(t => !user.CanSeeBag(t)))
        {
            throw new HubException("Bu bagaj için yetkiniz yok.");
        }

        foreach (var tag in bagTags)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, BagGroup(tag));
        }
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex BagTagPattern();
}
