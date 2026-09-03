using System.Text.Json;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlJobWorkerEndToEndTests(PostgreSqlClosureFixture fixture)
    : IClassFixture<PostgreSqlClosureFixture>
{
    [Fact]
    public async Task Transfer_WhenPlanIsSealedAndJobStarts_CopiesReferentiallyRequiredRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY); "
                + "CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES worker_parents(id)); "
                + "INSERT INTO worker_parents VALUES (2); INSERT INTO worker_children VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY); "
                + "CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES worker_parents(id));"
        );
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-pg-worker-" + Guid.NewGuid().ToString("N"));
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
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(
                completedScan.State == SchemaScanState.Completed,
                "Schema scan ended in " + completedScan.State + " (" + completedScan.FailureCode + ")."
            );
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
                            RawSql = "SELECT id AS \"__datapitcher_key_0\" FROM worker_children",
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    snapshotId,
                    scope.Schema,
                    "worker_children",
                    "pk_worker_children",
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
            await scope.ExecuteTargetAsync("INSERT INTO worker_parents VALUES (2);");
            var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
            Assert.Equal("sealed", review.Seal.Status);
            Assert.Equal(2, review.Totals.PlannedWrites);
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
                        + " ("
                        + job.FailureCode
                        + "). Events: "
                        + string.Join(", ", events.Events.Select(jobEvent => jobEvent.Payload.State))
                );
                Assert.Equal(
                    1L,
                    await scope.ScalarTargetAsync<long>(
                        "SELECT COUNT(*) FROM worker_children WHERE id = 1 AND parent_id = 2"
                    )
                );
                Assert.Equal(
                    1L,
                    await scope.ScalarTargetAsync<long>("SELECT COUNT(*) FROM worker_parents WHERE id = 2")
                );
                // The parent already existed in the target: it is skipped and reported, never a failure.
                var conflict = Assert.Single(events.Events, jobEvent => jobEvent.EventType == "conflict");
                Assert.Contains("worker_parents", conflict.Payload.Detail, StringComparison.Ordinal);
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
            "CREATE TABLE worker_nodes (id integer NOT NULL CONSTRAINT pk_worker_nodes PRIMARY KEY, parent_id integer NULL CONSTRAINT fk_worker_nodes_parent REFERENCES worker_nodes(id));";
        // 2,001 rows: every row points at the last one, which the 2,000-row batches only reach in the second batch.
        await scope.ExecuteAsync(
            ddl
                + " INSERT INTO worker_nodes (id, parent_id) VALUES (2001, NULL); INSERT INTO worker_nodes (id, parent_id) SELECT g, 2001 FROM generate_series(1, 2000) g;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_nodes", "pk_worker_nodes", "SELECT * FROM worker_nodes");

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2001L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_nodes"));
    }

    private static async Task<TransferJob> RunTransferAsync(
        PostgreSqlClosureScope scope,
        string rootTable,
        string primaryKey,
        string sql
    )
    {
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-pg-worker-" + Guid.NewGuid().ToString("N"));
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
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(
                completedScan.State == SchemaScanState.Completed,
                "Schema scan ended in " + completedScan.State + " (" + completedScan.FailureCode + ")."
            );
            var snapshotId =
                completedScan.SnapshotId
                ?? throw new InvalidOperationException("Source schema scan did not create a snapshot.");
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
                    snapshotId,
                    scope.Schema,
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
            var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
            Assert.Equal("sealed", review.Seal.Status);
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                var started2 = started;
                return await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started2.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
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
}
