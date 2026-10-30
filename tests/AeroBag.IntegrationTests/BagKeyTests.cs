using AeroBag.Api;

namespace AeroBag.IntegrationTests;

/// <summary>Yolcu jetonu ve canlı kanal anahtarı "etiket@uçuş" biçiminin ayrıştırılması (veritabanı gerekmez).</summary>
public sealed class BagKeyTests
{
    [Theory]
    [InlineData("0891000000@ZZ0100-20261030", "0891000000", "ZZ0100-20261030")]
    [InlineData("1234567890@A", "1234567890", "A")]
    public void Valid_keys_parse_and_round_trip(string raw, string tag, string flight)
    {
        Assert.True(BagKey.TryParse(raw, out var key));
        Assert.Equal(tag, key.BagTag);
        Assert.Equal(flight, key.FlightId);
        Assert.Equal(raw, key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0891000000")]                       // eski biçim: uçuşsuz etiket
    [InlineData("0891000000@")]                      // boş uçuş
    [InlineData("089100000@ZZ0100-20261030")]        // 9 hane
    [InlineData("08910000001@ZZ0100-20261030")]      // 11 hane
    [InlineData("089100000a@ZZ0100-20261030")]       // harf içeren etiket
    [InlineData("0891000000@zz0100-20261030")]       // küçük harf uçuş
    [InlineData("0891000000@ZZ0100 20261030")]       // boşluk
    [InlineData("0891000000@ZZ0100-20261030\n")]     // satır sonu
    [InlineData("0891000000@ZZ0100@20261030")]       // ikinci '@'
    [InlineData("٠٨٩١٠٠٠٠٠٠@ZZ0100-20261030")]       // ASCII olmayan rakamlar
    public void Malformed_keys_are_rejected(string? raw)
    {
        Assert.False(BagKey.TryParse(raw, out _));
    }

    [Fact]
    public void Flight_id_is_limited_to_the_column_length()
    {
        Assert.True(BagKey.TryParse("0891000000@" + new string('A', 40), out _));
        Assert.False(BagKey.TryParse("0891000000@" + new string('A', 41), out _));
    }
}
