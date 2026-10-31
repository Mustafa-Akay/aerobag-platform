using AeroBag.Infrastructure.Db;
using NpgsqlTypes;

namespace AeroBag.UnitTests;

/// <summary>
/// W12: ortak veritabanı yardımcıları. Davranışın W12 öncesindeki sınıf içi kopyalarla aynı kaldığını sabitler;
/// SQL tarafı mevcut entegrasyon testleriyle (olay işleme, kapı, çıkış süresi, uyarı, bant) doğrulanır.
/// </summary>
public class DbHelpersTests
{
    [Fact]
    public void P_turns_null_into_DBNull()
    {
        Assert.Equal(DBNull.Value, DbParams.P("x", null).Value);
        Assert.Equal("AB", DbParams.P("x", "AB").Value);
    }

    [Fact]
    public void V_is_typed_varchar_even_when_null()
    {
        var p = DbParams.V("x", null);
        Assert.Equal(NpgsqlDbType.Varchar, p.NpgsqlDbType);
        Assert.Equal(DBNull.Value, p.Value);
    }

    [Fact]
    public void Typed_parameters_keep_their_types()
    {
        Assert.Equal(NpgsqlDbType.Bytea, DbParams.PB("k", [1, 2, 3]).NpgsqlDbType);
        Assert.Equal(NpgsqlDbType.Jsonb, DbParams.J("pl", "{}").NpgsqlDbType);
    }

    [Fact]
    public void Bag_lock_key_format_is_unchanged()
    {
        // Biçim değişirse aynı bavula yazan eski ve yeni kod farklı kilit alır; bu yüzden sabitlenir.
        Assert.Equal("0891458921|ZZ0124-20261022", AdvisoryLock.BagKey("0891458921", "ZZ0124-20261022"));
    }

    [Fact]
    public void Outbox_topic_names_are_unchanged()
    {
        Assert.Equal("baggage.events", OutboxTopics.BaggageEvents);
        Assert.Equal("flight.context", OutboxTopics.FlightContext);
        Assert.Equal("bag.alerts", OutboxTopics.BagAlerts);
    }
}
