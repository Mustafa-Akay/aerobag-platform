using System.Security.Cryptography;
using System.Text;

namespace AeroBag.Infrastructure;

/// <summary>
/// Yolcu arama anahtarı: HMAC-SHA256(gizli anahtar, PNR | soyad). Veritabanında soyad ve PNR düz metin
/// tutulmaz; anahtarı bilmeyen biri tablodan yolcu çıkaramaz.
/// </summary>
public sealed class LookupKeyHasher
{
    private readonly byte[] _secret;

    public LookupKeyHasher(byte[] secret)
    {
        if (secret.Length < 16)
        {
            throw new ArgumentException("Gizli anahtar en az 16 bayt olmalı.", nameof(secret));
        }
        _secret = secret;
    }

    public byte[] Compute(string pnr, string surname) =>
        HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"{pnr.Trim().ToUpperInvariant()}|{NormalizeSurname(surname)}"));

    /// <summary>Büyük harfe çevirir; harf olmayan karakterleri (boşluk, tire, kesme) atar.</summary>
    public static string NormalizeSurname(string surname) =>
        new(surname.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
}
