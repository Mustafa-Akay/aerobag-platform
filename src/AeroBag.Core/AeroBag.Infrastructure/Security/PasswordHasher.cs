using System.Security.Cryptography;
using System.Text;

namespace AeroBag.Infrastructure.Security;

/// <summary>
/// Personel parolaları için PBKDF2-HMAC-SHA256. Saklama biçimi: <c>pbkdf2-sha256$yineleme$tuz(base64)$özet(base64)</c>.
/// Karşılaştırma sabit sürelidir. Bilinmeyen kullanıcıda da aynı maliyette doğrulama yapılır (<see cref="VerifyDummy"/>),
/// böylece yanıt süresinden kullanıcı adının var olup olmadığı anlaşılmaz.
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    // Rastgele bir parolanın özeti; yalnızca bilinmeyen kullanıcıda süreyi eşitlemek için.
    private const string Dummy = "pbkdf2-sha256$600000$Svx/dHfDJC7sXyER3lJr8A==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 10_000)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void VerifyDummy(string password) => _ = Verify(password, Dummy);
}
