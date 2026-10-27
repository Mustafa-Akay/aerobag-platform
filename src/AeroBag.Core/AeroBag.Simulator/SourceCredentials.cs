using System.Net.Http.Headers;
using System.Text;
using AeroBag.Application.Security;

namespace AeroBag.Simulator;

/// <summary>Simülatörün mesaj kaynağı kimliği. API'deki Ingest:Sources ile aynı ad ve gizli anahtar olmalıdır.</summary>
public sealed record SourceCredentials(string Id, string Secret)
{
    public static SourceCredentials Demo { get; } = new(RequestSignature.DemoSourceId, RequestSignature.DemoSourceSecret);

    /// <summary>İmzalı bir POST isteği oluşturur (gövde, zaman damgası, nonce ve HMAC imzası).</summary>
    public HttpRequestMessage SignedPost(string path, byte[] body, string contentType, TimeProvider? clock = null)
    {
        var ts = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var nonce = RequestSignature.NewNonce();
        var canonical = RequestSignature.Canonical("POST", path, ts, nonce, body);

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        req.Headers.Add(RequestSignature.SourceHeader, Id);
        req.Headers.Add(RequestSignature.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.Add(RequestSignature.NonceHeader, nonce);
        req.Headers.Add(RequestSignature.SignatureHeader, RequestSignature.Sign(Encoding.UTF8.GetBytes(Secret), canonical));
        return req;
    }
}
