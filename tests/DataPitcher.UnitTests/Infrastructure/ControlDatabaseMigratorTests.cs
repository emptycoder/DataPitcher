using LinqToDB.Data;
using DataPitcher.Infrastructure.Migrations;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseMigratorTests
{
    [Fact]
    public void ControlDatabaseMigrator_WhenAppliedTwice_RecordsEveryRegisteredMigrationExactlyOnce()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply(); fixture.Migrator.Apply();
        using var db = fixture.Database.Open();
        var scripts = ((ValueTuple<int, string>[])typeof(ControlDatabaseMigrator).GetField("Scripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!);
        Assert.Equal(scripts.Select(script => script.Item1), db.Query<int>("SELECT Version FROM SchemaVersion ORDER BY Version").ToList());
        Assert.Contains("JobLeases", db.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToList());
    }
}
