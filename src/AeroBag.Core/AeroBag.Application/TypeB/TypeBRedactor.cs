namespace AeroBag.Application.TypeB;

/// <summary>
/// Ham Type B metnini saklamadan önce kişisel alanları maskeler: <c>.P/</c> (yolcu adı) ve <c>.L/</c> (PNR).
/// Ayrıştırılamayan ya da kırpılmış mesajlarda da satır başına bakar; böylece ölü mektup kaydı da kişisel veri taşımaz.
/// Arama için gereken bilgi zaten HMAC özetinde (lookup_key) tutulur; ham metindeki ad ve PNR'a ihtiyaç yoktur.
/// </summary>
public static class TypeBRedactor
{
    public const string Mask = "***";

    private static readonly string[] PersonalPrefixes = [".P/", ".L/"];

    public static string Redact(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            foreach (var prefix in PersonalPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = prefix + Mask + (lines[i].EndsWith('\r') ? "\r" : "");
                    break;
                }
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Hata metninden (ör. "Alan .P biçimi geçersiz: 'KAYA/ALI'") ham mesajdaki kişisel değerleri temizler.
    /// Ölü mektup kaydının hata sütunu da kişisel veri taşımasın diye kullanılır.
    /// </summary>
    public static string ScrubText(string text, string? raw)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(raw))
        {
            return text;
        }

        var values = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (PersonalPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && trimmed.Length > 3)
            {
                var value = trimmed[3..];
                values.Add(value);
                values.AddRange(value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(v => v.Length >= 2));
            }
        }

        foreach (var v in values.Distinct().OrderByDescending(v => v.Length))
        {
            text = text.Replace(v, Mask, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
