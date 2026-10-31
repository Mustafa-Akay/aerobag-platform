using Npgsql;

namespace AeroBag.Infrastructure.Db;

/// <summary>
/// Bavul başına işlem kilidi (<c>pg_advisory_xact_lock</c>). Kilit işlem bitince kendiliğinden bırakılır.
/// Aynı bavula (etiket + uçuş) yazan her yol bu sınıfı kullanmalı; anahtar biçimi tek yerde durur.
/// </summary>
public static class AdvisoryLock
{
    /// <summary>Kilit anahtarı: <c>etiket|uçuş</c>. Biçim değişirse eski ve yeni sürüm aynı bavulu farklı kilitlerle korur.</summary>
    public static string BagKey(string bagTag, string flightId) => $"{bagTag}|{flightId}";

    /// <summary>Bavulun kilidini alır; başka bir işlem tutuyorsa bekler.</summary>
    public static async Task ForBagAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string bagTag, string flightId, CancellationToken ct)
    {
        await using var cmd = DbParams.Cmd(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
            DbParams.P("k", BagKey(bagTag, flightId)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
