using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseMigratorTests
{
    [Fact]
    public void ControlDatabaseMigrator_WhenAppliedTwice_RunsVersionOneExactlyOnce()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply(); fixture.Migrator.Apply();
        using var db = fixture.Database.Open();
        Assert.Equal([1], db.Query<int>("SELECT Version FROM SchemaVersion ORDER BY Version").ToList());
        Assert.Contains("JobLeases", db.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToList());
    }
}
