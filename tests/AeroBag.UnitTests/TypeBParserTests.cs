using AeroBag.Application.TypeB;

namespace AeroBag.UnitTests;

public class TypeBParserTests
{
    private static readonly DateOnly Ref = new(2026, 10, 22);

    private const string ValidBsm = """
        BSM
        .V/1LAYT
        .F/ZZ0124/22OCT/AYT/Y
        .N/0891458921001
        .W/K/1/18
        .P/YILMAZ/AYSE
        .L/AB3K7Q
        ENDBSM
        """;

    private const string ValidBum = """
        BUM
        .V/1LAYT
        .F/ZZ0124/22OCT/AYT
        .N/0891458921001
        .U/STAND104/20261022T211410Z
        ENDBUM
        """;

    private const string ValidBpm = """
        BPM
        .V/1LAYT
        .J/S/CHUTE04/22OCT/212100Z
        .F/ZZ0124/22OCT/AYT
        .N/0891458921001
        .Q/CAROUSEL04/BELT_ENTRY
        ENDBPM
        """;

    [Fact]
    public void Parses_valid_BSM()
    {
        var r = TypeBParser.Parse(ValidBsm, Ref);
        Assert.True(r.IsSuccess, r.Detail);
        var m = r.Message!;
        Assert.Equal(TypeBKind.Bsm, m.Kind);
        Assert.Equal("AYT", m.Airport);
        Assert.Equal("ZZ0124", m.FlightNumber);
        Assert.Equal(new DateOnly(2026, 10, 22), m.FlightDate);
        Assert.Equal("0891458921", m.BagTag);
        Assert.Equal("001", m.Sequence);
        Assert.Equal("Y", m.CabinClass);
        Assert.Equal(18, m.WeightKg);
        Assert.Equal("YILMAZ", m.Surname);
        Assert.Equal("AYSE", m.GivenName);
        Assert.Equal("AB3K7Q", m.Pnr);
    }

    [Fact]
    public void Parses_valid_BUM_with_stand_and_utc_time()
    {
        var m = TypeBParser.Parse(ValidBum, Ref).Message!;
        Assert.Equal(TypeBKind.Bum, m.Kind);
        Assert.Equal("STAND104", m.Stand);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 21, 14, 10, TimeSpan.Zero), m.OccurredAtUtc);
    }

    [Fact]
    public void Parses_valid_BPM_with_scan_point_and_carousel()
    {
        var m = TypeBParser.Parse(ValidBpm, Ref).Message!;
        Assert.Equal(TypeBKind.Bpm, m.Kind);
        Assert.Equal("CHUTE04", m.ScanPoint);
        Assert.Equal("CAROUSEL04", m.TargetCarousel);
        Assert.Equal("BELT_ENTRY", m.TargetPointType);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 21, 21, 0, TimeSpan.Zero), m.OccurredAtUtc);
    }

    [Fact]
    public void Accepts_windows_line_endings()
    {
        var r = TypeBParser.Parse(ValidBum.Replace("\n", "\r\n"), Ref);
        Assert.True(r.IsSuccess, r.Detail);
    }

    [Fact]
    public void Year_is_chosen_closest_to_reference_across_new_year()
    {
        var text = ValidBsm.Replace("22OCT", "02JAN");
        var m = TypeBParser.Parse(text, new DateOnly(2026, 12, 30)).Message!;
        Assert.Equal(new DateOnly(2027, 1, 2), m.FlightDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_input_is_rejected(string? text)
    {
        Assert.Equal(ParseError.Empty, TypeBParser.Parse(text, Ref).Error);
    }

    [Fact]
    public void Unknown_message_type_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace("BSM", "XXX"), Ref);
        Assert.Equal(ParseError.UnknownType, r.Error);
    }

    [Fact]
    public void Missing_end_line_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace("ENDBSM", ".L/AB3K7Q"), Ref);
        Assert.Equal(ParseError.MissingEnd, r.Error);
    }

    [Fact]
    public void End_line_mismatch_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace("ENDBSM", "ENDBUM"), Ref);
        Assert.Equal(ParseError.EndMismatch, r.Error);
    }

    [Fact]
    public void Missing_required_field_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(".L/AB3K7Q\n", ""), Ref);
        Assert.Equal(ParseError.MissingField, r.Error);
        Assert.Contains(".L", r.Detail);
    }

    [Fact]
    public void Duplicate_field_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(".L/AB3K7Q", ".L/AB3K7Q\n.L/ZZ9Z9Z"), Ref);
        Assert.Equal(ParseError.DuplicateField, r.Error);
    }

    [Fact]
    public void Unknown_field_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(".L/AB3K7Q", ".L/AB3K7Q\n.X/FOO"), Ref);
        Assert.Equal(ParseError.UnknownField, r.Error);
    }

    [Fact]
    public void Malformed_line_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(".W/K/1/18", "W K 1 18"), Ref);
        Assert.Equal(ParseError.MalformedLine, r.Error);
    }

    [Theory]
    [InlineData("22OCT", "31FEB")]
    [InlineData("22OCT", "22XXX")]
    [InlineData("22OCT", "00OCT")]
    public void Invalid_date_is_rejected(string from, string to)
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(from, to), Ref);
        Assert.Equal(ParseError.InvalidField, r.Error);
    }

    [Theory]
    [InlineData(".N/0891458921001", ".N/08914589")]
    [InlineData(".N/0891458921001", ".N/08914589210011")]
    [InlineData(".N/0891458921001", ".N/08914589AB001")]
    public void Bad_bag_tag_is_rejected(string from, string to)
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(from, to), Ref);
        Assert.Equal(ParseError.InvalidField, r.Error);
    }

    [Theory]
    [InlineData(".W/K/0/18")]
    [InlineData(".W/K/1/0")]
    [InlineData(".W/L/1/18")]
    public void Bad_weight_is_rejected(string replacement)
    {
        var r = TypeBParser.Parse(ValidBsm.Replace(".W/K/1/18", replacement), Ref);
        Assert.Equal(ParseError.InvalidField, r.Error);
    }

    [Fact]
    public void Bad_scan_time_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBpm.Replace("212100Z", "256100Z"), Ref);
        Assert.Equal(ParseError.InvalidField, r.Error);
    }

    [Fact]
    public void Too_long_line_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm.Replace("YILMAZ/AYSE", new string('A', 200) + "/AYSE"), Ref);
        Assert.Equal(ParseError.LineTooLong, r.Error);
    }

    [Fact]
    public void Too_long_message_is_rejected()
    {
        var r = TypeBParser.Parse(ValidBsm + new string(' ', TypeBParser.MaxTextLength), Ref);
        Assert.Equal(ParseError.TooLong, r.Error);
    }

    [Fact]
    public void Too_many_lines_is_rejected()
    {
        var text = "BSM\n" + string.Join("\n", Enumerable.Repeat(".W/K/1/18", 30)) + "\nENDBSM";
        Assert.Equal(ParseError.TooManyLines, TypeBParser.Parse(text, Ref).Error);
    }
}
