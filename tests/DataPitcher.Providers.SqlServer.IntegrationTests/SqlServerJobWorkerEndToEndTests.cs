using System.Text.Json;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerJobWorkerEndToEndTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Transfer_WhenPlanIsSealedAndJobStarts_CopiesReferentiallyRequiredRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY); CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.worker_parents(id)); INSERT dbo.worker_parents VALUES (2); INSERT dbo.worker_children VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY); CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.worker_parents(id));"
        );
        await EnableSnapshotIsolationAsync(scope.SourceAdminConnectionString, scope.Database);
        await EnableSnapshotIsolationAsync(scope.TargetAdminConnectionString, scope.Database);
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-job-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSecret = Path.Combine(directory, "source");
            var targetSecret = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(sourceSecret, scope.SourceConnectionString);
            await File.WriteAllTextAsync(targetSecret, scope.TargetConnectionString);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ControlDatabase:Path"] = Path.Combine(directory, "control.db"),
                        ["Secrets:Root"] = directory,
                        ["Worker:LeaseTtl"] = "00:00:30",
                        ["Worker:PollInterval"] = "00:00:00.050",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddDataPitcherComposition(configuration);
            using var provider = services.BuildServiceProvider();
            provider.ApplyControlDatabaseMigrations();
            var profiles = provider.GetRequiredService<ConnectionProfileStore>();
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
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.Equal(SchemaScanState.Completed, completedScan.State);
            var snapshotId =
                completedScan.SnapshotId
                ?? throw new InvalidOperationException("Source schema scan did not create a snapshot.");
            var selectionId = Guid.NewGuid();
            await provider
                .GetRequiredService<SelectionStore>()
                .SaveAsync(
                    selectionId,
                    "children",
                    JsonSerializer.Serialize(
                        new
                        {
                            Mode = "raw",
                            RawSql = "SELECT id AS [__datapitcher_key_0] FROM dbo.worker_children",
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    snapshotId,
                    "dbo",
                    "worker_children",
                    "PK_worker_children",
                    ["id"]
                );
            var application = provider.GetRequiredService<IDataPitcherApplication>();
            var planId = Guid.NewGuid();
            await application.SavePlanAsync(
                planId,
                new SavePlanRequest(
                    "worker plan",
                    null,
                    "\"0\"",
                    selectionId,
                    source.ConnectionId,
                    target.ConnectionId
                ),
                CancellationToken.None
            );
            await application.QueuePlanSealAsync(planId, CancellationToken.None);
            // The parent appears in the target after sealing (a re-run, or a concurrent writer): the transfer must
            // skip it and report it rather than fail on the primary key.
            await scope.ExecuteTargetAsync("INSERT dbo.worker_parents VALUES (2);");
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                var job = await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );

                var events = await provider
                    .GetRequiredService<IJobEventReader>()
                    .ReadAfterAsync(job.JobId, null, CancellationToken.None);
                Assert.True(
                    job.State is JobState.Succeeded,
                    "Expected job state Succeeded but was "
                        + job.State
                        + ". Events: "
                        + string.Join(", ", events.Events.Select(jobEvent => jobEvent.Payload.State))
                );
                Assert.Equal(
                    1,
                    await scope.ScalarTargetAsync<int>(
                        "SELECT COUNT(*) FROM dbo.worker_children WHERE id = 1 AND parent_id = 2"
                    )
                );
                Assert.Equal(
                    1,
                    await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_parents WHERE id = 2")
                );
                // The parent already existed in the target: it is skipped and reported, never a failure.
                var conflict = Assert.Single(events.Events, jobEvent => jobEvent.EventType == "conflict");
                Assert.Contains("dbo.worker_parents", conflict.Payload.Detail, StringComparison.Ordinal);
                Assert.Contains("1 row(s)", conflict.Payload.Detail, StringComparison.Ordinal);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Transfer_WhenATableReferencesItselfAcrossBatches_RelaxesAndRevalidatesForeignKeys()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_nodes (id int NOT NULL CONSTRAINT PK_worker_nodes PRIMARY KEY, parent_id int NULL CONSTRAINT FK_worker_nodes_parent FOREIGN KEY REFERENCES dbo.worker_nodes(id));";
        // 2,001 rows: every row points at the last one, which the 2,000-row batches only reach in the second batch.
        await scope.ExecuteAsync(
            ddl
                + " INSERT dbo.worker_nodes (id, parent_id) VALUES (2001, NULL); WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b) INSERT dbo.worker_nodes (id, parent_id) SELECT i, 2001 FROM n;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_nodes", "PK_worker_nodes", "SELECT * FROM dbo.worker_nodes");

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2001, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_nodes"));
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_worker_nodes_parent' AND is_disabled = 0 AND is_not_trusted = 0"
            )
        );
    }

    [Fact]
    public async Task Transfer_WhenTheSourceHoldsAnOrphan_EndsAsAVerificationFailureNamingTheTable()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_owners (id int NOT NULL CONSTRAINT PK_worker_owners PRIMARY KEY); CREATE TABLE dbo.worker_pets (id int NOT NULL CONSTRAINT PK_worker_pets PRIMARY KEY, owner_id int NOT NULL);";
        // The source constraint is not trusted, so an orphaned pet exists there; the target enforces it.
        await scope.ExecuteAsync(
            ddl
                + " ALTER TABLE dbo.worker_pets WITH NOCHECK ADD CONSTRAINT FK_worker_pets_owner FOREIGN KEY (owner_id) REFERENCES dbo.worker_owners(id); ALTER TABLE dbo.worker_pets NOCHECK CONSTRAINT FK_worker_pets_owner; INSERT dbo.worker_pets VALUES (1, 99);"
        );
        await scope.ExecuteTargetAsync(
            ddl
                + " ALTER TABLE dbo.worker_pets ADD CONSTRAINT FK_worker_pets_owner FOREIGN KEY (owner_id) REFERENCES dbo.worker_owners(id);"
        );

        var job = await RunTransferAsync(scope, "worker_pets", "PK_worker_pets", "SELECT * FROM dbo.worker_pets");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("verification_failed", job.FailureCode);
        Assert.Contains("dbo.worker_pets", job.FailureDetail, StringComparison.Ordinal);
    }

    /// <summary>Scans, selects every row of <paramref name="rootTable"/>, seals, runs the worker and waits for the end.</summary>
    private static async Task<TransferJob> RunTransferAsync(
        SqlServerClosureScope scope,
        string rootTable,
        string primaryKey,
        string sql
    )
    {
        await EnableSnapshotIsolationAsync(scope.SourceAdminConnectionString, scope.Database);
        await EnableSnapshotIsolationAsync(scope.TargetAdminConnectionString, scope.Database);
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-job-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSecret = Path.Combine(directory, "source");
            var targetSecret = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(sourceSecret, scope.SourceConnectionString);
            await File.WriteAllTextAsync(targetSecret, scope.TargetConnectionString);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ControlDatabase:Path"] = Path.Combine(directory, "control.db"),
                        ["Secrets:Root"] = directory,
                        ["Worker:LeaseTtl"] = "00:00:30",
                        ["Worker:PollInterval"] = "00:00:00.050",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddDataPitcherComposition(configuration);
            using var provider = services.BuildServiceProvider();
            provider.ApplyControlDatabaseMigrations();
            var profiles = provider.GetRequiredService<ConnectionProfileStore>();
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
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            await provider
                .GetServices<IHostedService>()
                .OfType<SchemaScanWorker>()
                .Single()
                .ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(completedScan.State == SchemaScanState.Completed, completedScan.FailureDetail);
            var selectionId = Guid.NewGuid();
            await provider
                .GetRequiredService<SelectionStore>()
                .SaveAsync(
                    selectionId,
                    rootTable,
                    JsonSerializer.Serialize(
                        new
                        {
                            Mode = "raw",
                            RawSql = sql,
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    completedScan.SnapshotId,
                    "dbo",
                    rootTable,
                    primaryKey,
                    ["id"]
                );
            var application = provider.GetRequiredService<IDataPitcherApplication>();
            var planId = Guid.NewGuid();
            await application.SavePlanAsync(
                planId,
                new SavePlanRequest(
                    "worker plan",
                    null,
                    "\"0\"",
                    selectionId,
                    source.ConnectionId,
                    target.ConnectionId
                ),
                CancellationToken.None
            );
            await application.QueuePlanSealAsync(planId, CancellationToken.None);
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                return await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<TransferJob> WaitForTerminalAsync(JobStore jobs, Guid jobId)
    {
        var last = jobs.Get(jobId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            last = jobs.Get(jobId);
            if (
                last.State is JobState.Cancelled or JobState.Succeeded or JobState.Failed or JobState.VerificationFailed
            )
                return last;
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not reach a terminal state. Last observed state: " + last.State + ".");
    }

    private static async Task EnableSnapshotIsolationAsync(string connectionString, string database)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "ALTER DATABASE [" + database + "] SET ALLOW_SNAPSHOT_ISOLATION ON;",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }
}
