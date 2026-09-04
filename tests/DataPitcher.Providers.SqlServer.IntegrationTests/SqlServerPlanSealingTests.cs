using System.Text.Json;
using DataPitcher.Application.Plans;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Time;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerPlanSealingTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task SealAsync_WhenRawParentSelectionRequiresChild_PersistsClosureAndFrozenKeys()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.seal_children (id int NOT NULL CONSTRAINT PK_seal_children PRIMARY KEY); CREATE TABLE dbo.seal_parents (id int NOT NULL CONSTRAINT PK_seal_parents PRIMARY KEY, child_id int NOT NULL REFERENCES dbo.seal_children(id)); INSERT dbo.seal_children VALUES (2); INSERT dbo.seal_parents VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.seal_children (id int NOT NULL CONSTRAINT PK_seal_children PRIMARY KEY); CREATE TABLE dbo.seal_parents (id int NOT NULL CONSTRAINT PK_seal_parents PRIMARY KEY, child_id int NOT NULL REFERENCES dbo.seal_children(id));"
        );
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-plan-sealing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSecret = Path.Combine(directory, "source");
            var targetSecret = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(sourceSecret, scope.SourceConnectionString);
            await File.WriteAllTextAsync(targetSecret, scope.TargetConnectionString);
            var database = new ControlDatabase("Data Source=" + Path.Combine(directory, "control.db"));
            var clock = new SystemClock();
            new ControlDatabaseMigrator(database, clock).Apply();
            var profiles = new ConnectionProfileStore(database, clock);
            var source = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "source",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    "dbo",
                    "dbo"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    "dbo",
                    "dbo"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = new SchemaSnapshotStore(database, clock);
            var schema = await new SqlServerSchemaIntrospector().ReadAsync(
                source,
                scope.SourceConnectionString,
                CancellationToken.None
            );
            var snapshotId = Guid.NewGuid();
            using (var control = database.Open())
                control.Execute(
                    "INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)",
                    new ControlParameter[]
                    {
                        new("snapshotId", snapshotId.ToString()),
                        new("connectionId", source.ConnectionId.ToString()),
                        new("snapshotHash", CanonicalSchemaSnapshotHasher.Hash(schema)),
                        new("contentJson", JsonSerializer.Serialize(schema)),
                        new("createdUtc", clock.UtcNow.ToString("O")),
                    }
                );
            var selections = new SelectionStore(database, clock);
            var selectionId = Guid.NewGuid();
            await selections.SaveAsync(
                selectionId,
                "parents",
                JsonSerializer.Serialize(
                    new
                    {
                        Mode = "raw",
                        RawSql = "SELECT id AS [__datapitcher_key_0] FROM dbo.seal_parents",
                        Parameters = Array.Empty<object>(),
                    }
                ),
                "\"0\"",
                CancellationToken.None,
                source.ConnectionId,
                snapshotId,
                "dbo",
                "seal_parents",
                "PK_seal_parents",
                ["id"]
            );
            var plans = new PlanStore(database, clock);
            var planId = Guid.NewGuid();
            await plans.SaveAsync(
                planId,
                "plan",
                null,
                "\"0\"",
                CancellationToken.None,
                selectionId,
                source.ConnectionId,
                target.ConnectionId
            );
            var sealing = new PlanSealingService(
                plans,
                selections,
                profiles,
                snapshots,
                new SecretReferenceResolver(directory),
                [new SqlServerSealingProvider()],
                new JobStore(database, clock)
            );

            await sealing.SealAsync(planId, CancellationToken.None);
            // Sealing the same plan again must rediscover the same rows rather than treat them as already staged.
            await sealing.SealAsync(planId, CancellationToken.None);

            var content = await plans.LoadContentAsync(planId, CancellationToken.None);
            Assert.NotNull(content);
            Assert.Contains(
                content!.Tables,
                table =>
                    table.Mapping.Source.Name == "seal_parents"
                    && table.State == DataPitcher.Core.Plans.PlanTableState.Root
                    && table.Manifest.Included == 1
            );
            Assert.Contains(
                content.Tables,
                table =>
                    table.Mapping.Source.Name == "seal_children"
                    && table.State == DataPitcher.Core.Plans.PlanTableState.RequiredDependency
                    && table.Manifest.Included == 1
            );
            var catalog = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync(
                "dbo",
                CancellationToken.None
            );
            var child = catalog.Table("seal_children").Definition;
            var keys = catalog.Tables.ToDictionary(
                table => table.Definition,
                table => StableKeySelector.Select(table.Definition, null)
            );
            await using var stages = new SqlServerStagingTables(
                scope.SourceConnectionString,
                scope.TargetConnectionString,
                catalog,
                keys,
                planId,
                false
            );

            Assert.Equal(
                1,
                await scope.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM " + SqlServerStagingTables.Qualified(stages.SourceTableName(child))
                )
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
