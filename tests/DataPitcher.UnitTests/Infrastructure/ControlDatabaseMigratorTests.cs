using DataPitcher.Infrastructure.Migrations;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseMigratorTests
{
    [Fact]
    public void ControlDatabaseMigrator_WhenAppliedTwice_RecordsEveryRegisteredMigrationExactlyOnce()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        fixture.Migrator.Apply();
        using var db = fixture.Database.Open();
        var scripts = (
            (ValueTuple<int, string>[])
                typeof(ControlDatabaseMigrator)
                    .GetField(
                        "Scripts",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                    )!
                    .GetValue(null)!
        );
        Assert.Equal(
            scripts.Select(script => script.Item1),
            db.Query<int>("SELECT Version FROM SchemaVersion ORDER BY Version").ToList()
        );
        Assert.Contains("JobLeases", db.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToList());
        Assert.Equal(
            [
                "ConnectionId",
                "DisplayName",
                "ProviderId",
                "SecretReferenceKind",
                "SecretReferenceLocator",
                "BusinessSchema",
                "StagingSchema",
                "Version",
                "HealthState",
                "AssessmentMode",
                "AssessmentRole",
                "DatabaseIdentity",
                "ProviderVersion",
                "CapabilitiesJson",
                "CleanupFailureCode",
                "CreatedUtc",
                "UpdatedUtc",
                "IdempotencyKey",
            ],
            db.Query<string>("SELECT name FROM pragma_table_info('ConnectionProfiles') ORDER BY cid").ToList()
        );
        Assert.Equal(
            [
                "ScanId",
                "ConnectionId",
                "IdempotencyKey",
                "State",
                "SnapshotId",
                "SnapshotHash",
                "FailureCode",
                "CreatedUtc",
                "UpdatedUtc",
            ],
            db.Query<string>("SELECT name FROM pragma_table_info('SchemaScans') ORDER BY cid").ToList()
        );
        Assert.Equal(
            ["SnapshotId", "ConnectionId", "SnapshotHash", "ContentJson", "CreatedUtc"],
            db.Query<string>("SELECT name FROM pragma_table_info('SchemaSnapshots') ORDER BY cid").ToList()
        );
    }
}
