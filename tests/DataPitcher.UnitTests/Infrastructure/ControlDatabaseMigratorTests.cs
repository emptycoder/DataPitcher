using DataPitcher.ControlStore;
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
                "FailureDetail",
            ],
            db.Query<string>("SELECT name FROM pragma_table_info('SchemaScans') ORDER BY cid").ToList()
        );
        Assert.Equal(
            ["SnapshotId", "ConnectionId", "SnapshotHash", "ContentJson", "CreatedUtc"],
            db.Query<string>("SELECT name FROM pragma_table_info('SchemaSnapshots') ORDER BY cid").ToList()
        );
    }

    [Fact]
    public void ControlDatabaseMigrator_ReplacesTheLegacyAppBusinessSchemaWithTheProviderDefault()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        using (var db = fixture.Database.Open())
        {
            foreach (var (id, provider) in new[] { ("1", "sqlserver"), ("2", "postgresql"), ("3", "sqlserver") })
                db.Execute(
                    "INSERT INTO ConnectionProfiles (ConnectionId, DisplayName, ProviderId, SecretReferenceKind, SecretReferenceLocator, BusinessSchema, StagingSchema, Version, HealthState, CreatedUtc, UpdatedUtc, IdempotencyKey) VALUES (@id, 'c', @provider, 'EnvironmentVariable', 'X', @schema, '__datapitcher', 1, 'Unknown', 't', 't', @id)",
                    new ControlParameter("id", id),
                    new ControlParameter("provider", provider),
                    new ControlParameter("schema", id == "3" ? "sales" : "app")
                );
            db.Execute("DELETE FROM SchemaVersion WHERE Version = 10");
        }

        fixture.Migrator.Apply();

        using var reopened = fixture.Database.Open();
        Assert.Equal(
            ["dbo", "public", "sales"],
            reopened.Query<string>("SELECT BusinessSchema FROM ConnectionProfiles ORDER BY ConnectionId").ToList()
        );
    }
}
