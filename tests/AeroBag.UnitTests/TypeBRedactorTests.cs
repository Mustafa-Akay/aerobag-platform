using AeroBag.Application.TypeB;

namespace AeroBag.UnitTests;

public class TypeBRedactorTests
{
    private const string Bsm = "BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/0891458921001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    [Fact]
    public void Name_and_pnr_lines_are_masked_and_the_rest_is_kept()
    {
        var r = TypeBRedactor.Redact(Bsm);
        Assert.DoesNotContain("YILMAZ", r);
        Assert.DoesNotContain("AYSE", r);
        Assert.DoesNotContain("AB3K7Q", r);
        Assert.Contains(".P/***", r);
        Assert.Contains(".L/***", r);
        Assert.Contains(".N/0891458921001", r);
        Assert.Contains(".F/ZZ0124/22OCT/AYT/Y", r);
        Assert.Equal(Bsm.Split('\n').Length, r.Split('\n').Length);
    }

    [Fact]
    public void Truncated_lowercase_and_crlf_messages_are_masked_too()
    {
        Assert.Equal("BSM\n.P/***", TypeBRedactor.Redact("BSM\n.P/YIL"));
        Assert.Equal("BSM\r\n.l/***\r\nENDBSM", TypeBRedactor.Redact("BSM\r\n.l/ab3k7q\r\nENDBSM").Replace(".L/", ".l/"));
        Assert.DoesNotContain("KAYA", TypeBRedactor.Redact("  .P/KAYA/ALI"));
    }

    [Fact]
    public void Messages_without_personal_lines_are_unchanged()
    {
        const string bum = "BUM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT\n.N/0891458921001\n.U/STAND104/20261022T121000Z\nENDBUM";
        Assert.Equal(bum, TypeBRedactor.Redact(bum));
        Assert.Equal("", TypeBRedactor.Redact(null));
    }

    [Fact]
    public void Personal_values_are_scrubbed_from_error_texts()
    {
        const string raw = "BSM\n.P/KAYA/ALI\n.L/ZX9Q2W\nENDBSM";
        var scrubbed = TypeBRedactor.ScrubText("Alan .P biçimi geçersiz: 'KAYA/ALI'. Ayrıca ZX9Q2W, kaya.", raw);
        Assert.DoesNotContain("KAYA", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALI", scrubbed);
        Assert.DoesNotContain("ZX9Q2W", scrubbed);
        Assert.Equal("hata", TypeBRedactor.ScrubText("hata", null));
    }
}
