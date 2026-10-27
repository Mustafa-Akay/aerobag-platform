using System.Globalization;
using System.Text;
using AeroBag.Application.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroBag.IntegrationTests;

/// <summary>Gerçek API'yi test veritabanıyla başlatır ve imzalı istek oluşturur.</summary>
internal static class TestApi
{
    public const string SourceId = "test-source";
    public const string SourceSecret = "integration-test-source-secret-0123456789";
    public const string ContextOnlyId = "aodb-only";
    public const string ContextOnlySecret = "integration-test-aodb-secret-0123456789ab";

    public static WebApplicationFactory<Program> Create(PostgresFixture fx) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:Default", fx.ConnectionString);
        b.UseSetting("Ml:BaseUrl", "");
        b.UseSetting("Auth:SigningKey", "integration-test-signing-key-0123456789abcdef");
        b.UseSetting("Ingest:Sources:0:Id", SourceId);
        b.UseSetting("Ingest:Sources:0:Secret", SourceSecret);
        b.UseSetting("Ingest:Sources:0:Scopes", "events,context,gate");
        b.UseSetting("Ingest:Sources:1:Id", ContextOnlyId);
        b.UseSetting("Ingest:Sources:1:Secret", ContextOnlySecret);
        b.UseSetting("Ingest:Sources:1:Scopes", "context");
    });

    public static HttpRequestMessage Signed(string path, string body, string contentType = "text/plain",
        string sourceId = SourceId, string secret = SourceSecret, long? unixSeconds = null, string? nonce = null, string? sendBody = null)
    {
        var ts = unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var n = nonce ?? RequestSignature.NewNonce();
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = RequestSignature.Sign(Encoding.UTF8.GetBytes(secret), RequestSignature.Canonical("POST", path, ts, n, bytes));

        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(sendBody ?? body, Encoding.UTF8, contentType) };
        req.Headers.Add(RequestSignature.SourceHeader, sourceId);
        req.Headers.Add(RequestSignature.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        req.Headers.Add(RequestSignature.NonceHeader, n);
        req.Headers.Add(RequestSignature.SignatureHeader, signature);
        return req;
    }
}
