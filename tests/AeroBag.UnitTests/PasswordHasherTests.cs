using AeroBag.Infrastructure.Security;

namespace AeroBag.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_round_trips_and_rejects_wrong_password()
    {
        var stored = PasswordHasher.Hash("dogru-parola", iterations: 20_000);
        Assert.StartsWith("pbkdf2-sha256$20000$", stored);
        Assert.True(PasswordHasher.Verify("dogru-parola", stored));
        Assert.False(PasswordHasher.Verify("Dogru-parola", stored));
    }

    [Fact]
    public void Same_password_gets_a_different_salt_each_time()
    {
        Assert.NotEqual(PasswordHasher.Hash("x", 20_000), PasswordHasher.Hash("x", 20_000));
    }

    [Fact]
    public void Demo_hash_produced_outside_dotnet_is_compatible()
    {
        // Python hashlib.pbkdf2_hmac('sha256', ..., 600000, 32) ile üretildi.
        const string stored = "pbkdf2-sha256$600000$A+h9Hgw50yiWLG0sbM2lqQ==$6d+uwsPTovCxPJR12QYXS+qMbRN+XiIxBNGNuooY5Uk=";
        Assert.True(PasswordHasher.Verify("ops-demo-2026", stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$600000$not-base64!$AAAA")]
    [InlineData("pbkdf2-sha256$100$AAAA$AAAA")]
    [InlineData("md5$600000$AAAA$AAAA")]
    public void Malformed_stored_values_never_verify(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }
}
