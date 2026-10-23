using System.Text;
using AeroBag.Infrastructure;

namespace AeroBag.UnitTests;

public class LookupKeyHasherTests
{
    private static LookupKeyHasher Hasher(string secret = "unit-test-secret-0123456789") =>
        new(Encoding.UTF8.GetBytes(secret));

    [Fact]
    public void Same_input_gives_same_32_byte_key()
    {
        var key = Hasher().Compute("AB3K7Q", "YILMAZ");
        Assert.Equal(key, Hasher().Compute("AB3K7Q", "YILMAZ"));
        Assert.Equal(32, key.Length);
    }

    [Theory]
    [InlineData("ab3k7q", "yilmaz")]
    [InlineData(" AB3K7Q ", "Yil Maz")]
    [InlineData("AB3K7Q", "YILMAZ-")]
    public void Key_ignores_case_whitespace_and_punctuation(string pnr, string surname)
    {
        Assert.Equal(Hasher().Compute("AB3K7Q", "YILMAZ"), Hasher().Compute(pnr, surname));
    }

    [Fact]
    public void Different_pnr_surname_or_secret_gives_different_key()
    {
        var baseKey = Hasher().Compute("AB3K7Q", "YILMAZ");
        Assert.NotEqual(baseKey, Hasher().Compute("AB3K7R", "YILMAZ"));
        Assert.NotEqual(baseKey, Hasher().Compute("AB3K7Q", "KAYA"));
        Assert.NotEqual(baseKey, Hasher("another-secret-0123456789").Compute("AB3K7Q", "YILMAZ"));
    }

    [Fact]
    public void Short_secret_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LookupKeyHasher(new byte[8]));
    }
}
