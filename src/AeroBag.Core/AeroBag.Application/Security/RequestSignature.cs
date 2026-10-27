using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AeroBag.Application.Security;

/// <summary>
/// Mesaj kaynaklarının (simülatör, AODB, kapı sensörü) API'ye gönderdiği isteklerin imzası.
/// İmzalanan metin: <c>v1\n{unix saniye}\n{nonce}\n{METHOD}\n{yol ve sorgu}\n{gövdenin SHA-256'sı (hex)}</c>.
/// İmza: <c>v1=</c> + HMAC-SHA256(kaynağın gizli anahtarı, imzalanan metin) (küçük harf hex).
/// Zaman damgası tekrar oynatma penceresini sınırlar, nonce pencere içindeki tekrarı engeller.
/// </summary>
public static partial class RequestSignature
{
    public const string SourceHeader = "X-AeroBag-Source";
    public const string TimestampHeader = "X-AeroBag-Timestamp";
    public const string NonceHeader = "X-AeroBag-Nonce";
    public const string SignatureHeader = "X-AeroBag-Signature";
    public const string Version = "v1";

    /// <summary>Demo kaynağı: yalnızca yerel demo içindir. Yapılandırmada kaynak verilince API bunu kabul etmez.</summary>
    public const string DemoSourceId = "simulator";
    public const string DemoSourceSecret = "dev-only-simulator-source-secret-change-me";

    public static string Canonical(string method, string pathAndQuery, long unixSeconds, string nonce, ReadOnlySpan<byte> body) =>
        $"{Version}\n{unixSeconds}\n{nonce}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{Convert.ToHexStringLower(SHA256.HashData(body))}";

    public static string Sign(byte[] secret, string canonical) =>
        $"{Version}=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)));

    /// <summary>Sabit süreli karşılaştırma; uzunluk farkında da false döner.</summary>
    public static bool Verify(byte[] secret, string canonical, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, canonical)), Encoding.UTF8.GetBytes(presented));

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidNonce(string? nonce) => nonce is not null && NoncePattern().IsMatch(nonce);

    public static bool IsValidSourceId(string? id) => id is not null && SourceIdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex NoncePattern();

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex SourceIdPattern();
}
