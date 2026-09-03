using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerRunSessionsTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Sessions_WhenFrozenKeysAreRead_MovesOnlyPlannedRowsAndAdvancesTheCheckpoint()
    {
        await using var scope = await fixture.CreateScopeAsync();
        using var setup = await SetupAsync(scope);
        var sessions = setup.Sessions;
        var lease = Lease(setup.Run.JobId, 1);

        await using var target = await sessions.OpenAsync(setup.Run, CancellationToken.None);
        var checkpoint = (await target.AcquireFenceReadCheckpointAndJournalAsync(setup.Run, lease, CancellationToken.None)).Checkpoint;
        Assert.Empty(await target.RepairMutationsAsync([], CancellationToken.None));
        await target.QuarantineAsync(new TargetMutation("unused", "unused", TargetMutationKind.DisabledTrigger), "unused", CancellationToken.None);
        await using var source = await sessions.OpenKeysetAsync(setup.Run, checkpoint.LastStableKey, CancellationToken.None, checkpoint.LastTable);
        for (TransferUnit? unit; (unit = await source.ReadNextAsync(CancellationToken.None)) is not null;)
            checkpoint = await target.ApplyAsync(setup.Run, lease, unit, CancellationToken.None);
        await source.DiscardUncommittedAsync(CancellationToken.None);
        await target.DiscardUncommittedAsync(CancellationToken.None);

        Assert.Equal(3, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.run_rows"));
        Assert.Equal("three", await scope.ScalarTargetAsync<string>("SELECT code FROM dbo.run_rows WHERE id=3"));
        Assert.Equal(2, checkpoint.BatchSequence);
        Assert.Equal(setup.SourceTable, checkpoint.LastTable);
        Assert.Equal(1, (await new SqlServerTargetCheckpointStore(scope.TargetConnectionString).ReadAsync(setup.Run.JobId, setup.Run.RunId, CancellationToken.None))!.LastBatchSequence);
    }

    [Fact]
    public async Task Sessions_WhenResumingFromAMidTransferCheckpoint_NeitherSkipsNorDuplicatesRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        using var setup = await SetupAsync(scope);

        await using (var target = await setup.Sessions.OpenAsync(setup.Run, CancellationToken.None))
        {
            var checkpoint = (await target.AcquireFenceReadCheckpointAndJournalAsync(setup.Run, Lease(setup.Run.JobId, 1), CancellationToken.None)).Checkpoint;
            await using var source = await setup.Sessions.OpenKeysetAsync(setup.Run, checkpoint.LastStableKey, CancellationToken.None, checkpoint.LastTable);
            var first = await source.ReadNextAsync(CancellationToken.None);
            Assert.NotNull(first);
            checkpoint = await target.ApplyAsync(setup.Run, Lease(setup.Run.JobId, 1), first!, CancellationToken.None);
            Assert.Equal(1, checkpoint.BatchSequence);
            Assert.Equal(2, checkpoint.LastStableKey!.Components.Single().Value);
        }

        await using var resumedTarget = await setup.Sessions.OpenAsync(setup.Run, CancellationToken.None);
        var resumed = (await resumedTarget.AcquireFenceReadCheckpointAndJournalAsync(setup.Run, Lease(setup.Run.JobId, 2), CancellationToken.None)).Checkpoint;
        await using var resumedSource = await setup.Sessions.OpenKeysetAsync(setup.Run, resumed.LastStableKey, CancellationToken.None, resumed.LastTable);
        var next = await resumedSource.ReadNextAsync(CancellationToken.None);
        Assert.NotNull(next);
        var final = await resumedTarget.ApplyAsync(setup.Run, Lease(setup.Run.JobId, 2), next!, CancellationToken.None);
        Assert.Null(await resumedSource.ReadNextAsync(CancellationToken.None));

        Assert.Equal(3, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.run_rows"));
        Assert.Equal(3, await scope.ScalarTargetAsync<int>("SELECT COUNT(DISTINCT id) FROM dbo.run_rows"));
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT MIN(id) FROM dbo.run_rows"));
        Assert.Equal(3, await scope.ScalarTargetAsync<int>("SELECT MAX(id) FROM dbo.run_rows"));
        Assert.Equal(2, final.BatchSequence);
        Assert.Equal(1, (await new SqlServerTargetCheckpointStore(scope.TargetConnectionString).ReadAsync(setup.Run.JobId, setup.Run.RunId, CancellationToken.None))!.LastBatchSequence);
    }

    private static async Task<RunSetup> SetupAsync(SqlServerClosureScope scope)
    {
        await scope.ExecuteAsync("CREATE TABLE dbo.run_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL); INSERT dbo.run_rows VALUES (1,N'one'),(2,N'two'),(3,N'three'),(4,N'unplanned');");
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.run_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);");
        var sourceTable = new TableAddress("dbo", "run_rows");
        var control = new Control();
        var profiles = new ConnectionProfileStore(control.Database, control.Clock);
        var source = await profiles.CreateAsync(new ConnectionProfileDraft("Source", "sqlserver", new(SecretReferenceKind.EnvironmentVariable, "source"), "dbo", "dbo"), "source", CancellationToken.None);
        var target = await profiles.CreateAsync(new ConnectionProfileDraft("Target", "sqlserver", new(SecretReferenceKind.EnvironmentVariable, "target"), "dbo", "dbo"), "target", CancellationToken.None);
        var plans = new PlanStore(control.Database, control.Clock);
        var planId = Guid.NewGuid();
        _ = await plans.SaveAsync(planId, "Run session plan", null, "\"0\"", CancellationToken.None, sourceConnectionId: source.ConnectionId, targetConnectionId: target.ConnectionId);
        var content = Plan(sourceTable, source.ConnectionId, target.ConnectionId);
        await plans.SealAsync(planId, content, CancellationToken.None);
        var schema = await new SqlServerCatalogReader(scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None);
        var definition = schema.Table("run_rows").Definition;
        var keys = new Dictionary<DataPitcher.Core.Schema.TableDefinition, DataPitcher.Core.Schema.StableKeySelection> { [definition] = DataPitcher.Core.Schema.StableKeySelector.Select(definition, null) };
        var frozen = new SqlServerStagingTables(scope.SourceConnectionString, scope.TargetConnectionString, schema, keys, planId, false);
        await frozen.InsertSourceAsync(definition, [new StableKey([new KeyComponent("id", 1)]), new StableKey([new KeyComponent("id", 2)]), new StableKey([new KeyComponent("id", 3)])], 0, CancellationToken.None);
        await frozen.DisposeAsync();
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var run = await new PlanJobRunCatalog(plans).LoadAsync(new TransferJob(jobId, runId, planId, "run-session", JobState.Queued), CancellationToken.None);
        return new RunSetup(control, sourceTable, run, new SqlServerRunSessions(plans, profiles, new Resolver(scope.SourceConnectionString, scope.TargetConnectionString)));
    }

    private static TransferPlanContent Plan(TableAddress table, Guid sourceConnectionId, Guid targetConnectionId) => new(
        new ConnectionFingerprint("sqlserver", "source", "source", sourceConnectionId),
        new ConnectionFingerprint("sqlserver", "target", "target", targetConnectionId),
        new SchemaSnapshotReference("source"), new SchemaSnapshotReference("target"), [], [], [new TableConflictPolicy(table, RootConflictPolicy.FailOnConflict)],
        ConsistencyMode.FrozenKeys, TransferMode.ResumableStaged, TriggerStrategy.Fire, ConstraintStrategy.Enforce,
        [new StableKeyDefinition(table, "PK_run_rows", ["id"])],
        [new PlanTable(new TableMapping(table, table, [new ColumnMapping("id", "id"), new ColumnMapping("code", "code")]), PlanTableState.Root, new ManifestCounts(3, 3, 0, 0), new TopologicalGroup([table]), CycleStrategy.NotApplicable)],
        new BatchTarget(2, 64), VerificationStrategy.Standard, new ManifestCounts(3, 3, 0, 0));

    private static LeaseGrant Lease(Guid jobId, long fenceToken) => new(jobId, "worker", fenceToken, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow);

    private sealed class RunSetup(Control control, TableAddress sourceTable, TransferRun run, SqlServerRunSessions sessions) : IDisposable
    {
        public TableAddress SourceTable { get; } = sourceTable;
        public TransferRun Run { get; } = run;
        public SqlServerRunSessions Sessions { get; } = sessions;
        public void Dispose() => control.Dispose();
    }

    private sealed class Resolver(string source, string target) : ISecretReferenceResolver
    {
        public Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(StringComparer.Ordinal.Equals(reference.Locator, "source") ? source : target);
    }

    private sealed class Control : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"datapitcher-run-sessions-{Guid.NewGuid():N}.db");
        public Clock Clock { get; } = new();
        public ControlDatabase Database { get; }
        public Control()
        {
            Database = new ControlDatabase($"Data Source={_path}");
            new ControlDatabaseMigrator(Database, Clock).Apply();
        }
        public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; } = DateTimeOffset.UnixEpoch; }
}
