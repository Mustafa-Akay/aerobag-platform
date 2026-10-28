using AeroBag.Infrastructure.Migrations;

namespace AeroBag.UnitTests;

public class MigrationSetTests
{
    [Fact]
    public void Files_are_ordered_by_version()
    {
        var set = MigrationSet.From([("0002_b.sql", "b"), ("0001_a.sql", "a")]);
        Assert.Equal(new[] { "0001_a", "0002_b" }, set.Select(m => m.ToString()));
    }

    [Theory]
    [InlineData("1_init.sql")]
    [InlineData("0001-init.sql")]
    [InlineData("0001_Init.sql")]
    [InlineData("0001_init.txt")]
    public void Bad_file_names_are_rejected(string name)
    {
        Assert.Throws<MigrationException>(() => MigrationSet.From([(name, "x")]));
    }

    [Fact]
    public void Gaps_and_duplicates_are_rejected()
    {
        Assert.Throws<MigrationException>(() => MigrationSet.From([("0001_a.sql", "a"), ("0003_c.sql", "c")]));
        Assert.Throws<MigrationException>(() => MigrationSet.From([("0001_a.sql", "a"), ("0001_b.sql", "b")]));
        Assert.Throws<MigrationException>(() => MigrationSet.From([("0002_b.sql", "b")]));
    }

    [Fact]
    public void Checksum_ignores_line_endings_and_bom_but_not_content()
    {
        Assert.Equal(Migration.ComputeChecksum("a;\nb;"), Migration.ComputeChecksum("﻿a;\r\nb;"));
        Assert.NotEqual(Migration.ComputeChecksum("a;"), Migration.ComputeChecksum("a; "));
    }

    [Fact]
    public void Embedded_migrations_start_with_the_initial_schema()
    {
        var all = EmbeddedMigrations.Load();
        Assert.NotEmpty(all);
        Assert.Equal("0001_init", all[0].ToString());
        Assert.Contains("CREATE TABLE baggage_events", all[0].Sql);
        Assert.Equal(all.Count, EmbeddedMigrations.LatestVersion);
    }
}
