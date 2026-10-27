using System.Text;
using AeroBag.Application.Security;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

public class RequestSignatureTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret-0123456789abcdef");
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("BSM\n.V/1LAYT\nENDBSM");
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Matches_a_vector_computed_outside_dotnet()
    {
        // Python: hmac.new(secret, canonical, sha256) ile üretildi; imza biçimi dile bağlı değildir.
        var canonical = RequestSignature.Canonical("post", "/v1/events", 1761572400, Nonce, Body);
        Assert.Equal("v1\n1761572400\n" + Nonce + "\nPOST\n/v1/events\n9117a403691d0eb61cb793cca3a860aed518c9bf58da3d4e5b4353d1e033e1b5", canonical);
        Assert.Equal("v1=e4dee02d6d07bb9e47ca176e5f80762eb2391ca2172f6aebd636b5105eb6b3ff", RequestSignature.Sign(Secret, canonical));
    }

    [Fact]
    public void Any_changed_part_breaks_the_signature()
    {
        var good = RequestSignature.Sign(Secret, RequestSignature.Canonical("POST", "/v1/events", 100, Nonce, Body));
        Assert.True(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/events", 100, Nonce, Body), good));
        Assert.False(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/flights/context", 100, Nonce, Body), good));
        Assert.False(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/events", 101, Nonce, Body), good));
        Assert.False(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/events", 100, Nonce.Replace('0', '1'), Body), good));
        Assert.False(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/events", 100, Nonce, "x"u8), good));
        Assert.False(RequestSignature.Verify(Encoding.UTF8.GetBytes("another-secret-0123456789abcd"), RequestSignature.Canonical("POST", "/v1/events", 100, Nonce, Body), good));
        Assert.False(RequestSignature.Verify(Secret, RequestSignature.Canonical("POST", "/v1/events", 100, Nonce, Body), good[..^1]));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("short", false)]
    [InlineData("has space 0123456789", false)]
    [InlineData(null, false)]
    public void Nonce_format_is_enforced(string? nonce, bool valid) => Assert.Equal(valid, RequestSignature.IsValidNonce(nonce));

    [Theory]
    [InlineData("simulator", true)]
    [InlineData("aodb-1", true)]
    [InlineData("Simulator", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Source_id_format_is_enforced(string id, bool valid) => Assert.Equal(valid, RequestSignature.IsValidSourceId(id));

    [Fact]
    public void Simulator_requests_carry_a_verifiable_signature()
    {
        var creds = new SourceCredentials("simulator", "simulator-secret-0123456789abcdef");
        using var req = creds.SignedPost("/v1/events", Body, "text/plain; charset=utf-8");

        var ts = long.Parse(req.Headers.GetValues(RequestSignature.TimestampHeader).Single());
        var nonce = req.Headers.GetValues(RequestSignature.NonceHeader).Single();
        var signature = req.Headers.GetValues(RequestSignature.SignatureHeader).Single();
        Assert.Equal("simulator", req.Headers.GetValues(RequestSignature.SourceHeader).Single());
        Assert.True(RequestSignature.IsValidNonce(nonce));
        Assert.True(RequestSignature.Verify(Encoding.UTF8.GetBytes(creds.Secret), RequestSignature.Canonical("POST", "/v1/events", ts, nonce, Body), signature));
    }
}
