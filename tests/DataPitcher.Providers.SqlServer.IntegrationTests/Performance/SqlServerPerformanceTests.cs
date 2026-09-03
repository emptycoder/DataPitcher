using System.Globalization;
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
using Xunit.Abstractions;

namespace DataPitcher.Providers.SqlServer.IntegrationTests.Performance;

/// <summary>
/// Times the operations operators wait on: scanning a schema, sealing a plan (dependency closure) and running the
/// transfer, on a three-level graph (orders → customers → countries). Opt in with DATAPITCHER_PERF=1; size the graph
/// with DATAPITCHER_PERF_ROWS. Numbers land in artifacts/performance/results.jsonl.
/// </summary>
[Collection("SqlServer closure")]
[Trait("Category", "Performance")]
public sealed class SqlServerPerformanceTests(SqlServerClosureFixture fixture, ITestOutputHelper output)
{
    private const string Schema =
        "CREATE TABLE dbo.perf_countries (code int NOT NULL CONSTRAINT PK_perf_countries PRIMARY KEY, name nvarchar(64) NOT NULL);"
        + "CREATE TABLE dbo.perf_customers (id int NOT NULL CONSTRAINT PK_perf_customers PRIMARY KEY, country_code int NOT NULL CONSTRAINT FK_perf_customers_countries REFERENCES dbo.perf_countries(code), name nvarchar(64) NOT NULL);"
        + "CREATE TABLE dbo.perf_orders (id int NOT NULL CONSTRAINT PK_perf_orders PRIMARY KEY, customer_id int NOT NULL CONSTRAINT FK_perf_orders_customers REFERENCES dbo.perf_customers(id), amount decimal(18,2) NOT NULL, note nvarchar(200) NOT NULL);";

    [PerformanceFact]
    public async Task ScanSealAndTransfer_OrdersGraph()
    {
        var rows = PerformanceReport.Rows;
        var customers = Math.Max(1, Math.Min(2_000, rows / 10));
        var report = new PerformanceReport(output, "sqlserver", "orders-graph");
        report.Metric("rows", rows);
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(Schema);
        await scope.ExecuteTargetAsync(Schema);
        await scope.ExecuteAsync(
            Numbers(50)
                + " INSERT dbo.perf_countries (code, name) SELECT i, CONCAT(N'Country ', i) FROM n;"
                + Numbers(customers)
                + $" INSERT dbo.perf_customers (id, country_code, name) SELECT i, (i % 50) + 1, CONCAT(N'Customer ', i) FROM n;"
                + Numbers(rows)
                + $" INSERT dbo.perf_orders (id, customer_id, amount, note) SELECT i, (i % {customers}) + 1, i * 1.25, CONCAT(N'Order number ', i, N' with a note long enough to look like real data') FROM n;"
        );
        await EnableSnapshotIsolationAsync(scope.SourceAdminConnectionString, scope.Database);
        await EnableSnapshotIsolationAsync(scope.TargetAdminConnectionString, scope.Database);
        await using var host = await PerformanceHost.CreateAsync(
            scope.SourceConnectionString,
            scope.TargetConnectionString
        );

        var snapshotId = await report.TimeAsync("scan", () => host.ScanAsync());
        var selectionId = await host.SaveSelectionAsync(
            snapshotId,
            "SELECT * FROM dbo.perf_orders",
            "perf_orders",
            "PK_perf_orders"
        );
        var planId = await host.SavePlanAsync(selectionId);
        await report.TimeAsync("seal", () => host.SealAsync(planId));
        var review = await host.Application.GetPlanReviewAsync(planId, CancellationToken.None);
        report.Metric("plannedWrites", review.Totals.PlannedWrites);
        var job = await report.TimeAsync("transfer", () => host.TransferAsync(planId, PerformanceReport.Budget));

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(rows, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.perf_orders"));
        Assert.Equal(customers, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.perf_customers"));
        Assert.Equal(50, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.perf_countries"));
        report.Metric("rowsWritten", host.RowsWritten);
        report.Metric(
            "rowsPerSecond",
            Math.Round(host.RowsWritten / Math.Max(0.001, host.TransferDuration.TotalSeconds))
        );
        await report.CompleteAsync();
    }

    [PerformanceFact]
    public async Task Scan_WideSchema()
    {
        const int tables = 300;
        var report = new PerformanceReport(output, "sqlserver", "wide-schema-scan");
        report.Metric("tables", tables);
        await using var scope = await fixture.CreateScopeAsync();
        var ddl = string.Join(
            " ",
            Enumerable
                .Range(1, tables)
                .Select(index =>
                    $"CREATE TABLE dbo.wide_{index} (id int NOT NULL CONSTRAINT PK_wide_{index} PRIMARY KEY, parent_id int NULL{(index > 1 ? $" CONSTRAINT FK_wide_{index} REFERENCES dbo.wide_{index - 1}(id)" : "")}, a nvarchar(50) NULL, b decimal(10,2) NULL, c datetime2 NULL);"
                )
        );
        await scope.ExecuteAsync(ddl);
        await using var host = await PerformanceHost.CreateAsync(
            scope.SourceConnectionString,
            scope.TargetConnectionString
        );

        var snapshotId = await report.TimeAsync("scan", () => host.ScanAsync());
        var snapshot = await host.Snapshots.GetAsync(host.SourceConnectionId, snapshotId, CancellationToken.None);

        Assert.True(snapshot.Content.Tables.Count >= tables);
        await report.CompleteAsync();
    }

    private static string Numbers(int count) =>
        $"WITH n AS (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)";

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

/// <summary>The production composition wired to one source and one target, driven the way the API drives it.</summary>
internal sealed class PerformanceHost : IAsyncDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _provider;
    private readonly JobWorker _worker;

    private PerformanceHost(string directory, ServiceProvider provider, Guid source, Guid target)
    {
        _directory = directory;
        _provider = provider;
        SourceConnectionId = source;
        TargetConnectionId = target;
        _worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
    }

    public Guid SourceConnectionId { get; }
    public Guid TargetConnectionId { get; }
    public IDataPitcherApplication Application => _provider.GetRequiredService<IDataPitcherApplication>();
    public SchemaSnapshotStore Snapshots => _provider.GetRequiredService<SchemaSnapshotStore>();
    public long RowsWritten { get; private set; }
    public TimeSpan TransferDuration { get; private set; }

    public static async Task<PerformanceHost> CreateAsync(string sourceConnectionString, string targetConnectionString)
    {
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourceSecret = Path.Combine(directory, "source");
        var targetSecret = Path.Combine(directory, "target");
        await File.WriteAllTextAsync(sourceSecret, sourceConnectionString);
        await File.WriteAllTextAsync(targetSecret, targetConnectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ControlDatabase:Path"] = Path.Combine(directory, "control.db"),
                    ["Secrets:Root"] = directory,
                    ["Worker:LeaseTtl"] = "00:01:00",
                    ["Worker:PollInterval"] = "00:00:00.050",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddDataPitcherComposition(configuration);
        var provider = services.BuildServiceProvider();
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
        return new PerformanceHost(directory, provider, source.ConnectionId, target.ConnectionId);
    }

    public async Task<Guid> ScanAsync()
    {
        var scan = await Snapshots.QueueAsync(SourceConnectionId, Guid.NewGuid().ToString("N"), CancellationToken.None);
        await _provider
            .GetServices<IHostedService>()
            .OfType<SchemaScanWorker>()
            .Single()
            .ProcessNextAsync(CancellationToken.None);
        var completed = await Snapshots.GetScanAsync(SourceConnectionId, scan.ScanId, CancellationToken.None);
        Assert.True(completed.State == SchemaScanState.Completed, "Scan failed: " + completed.FailureDetail);
        return completed.SnapshotId!.Value;
    }

    public async Task<Guid> SaveSelectionAsync(Guid snapshotId, string sql, string rootTable, string primaryKey)
    {
        var selectionId = Guid.NewGuid();
        await _provider
            .GetRequiredService<SelectionStore>()
            .SaveAsync(
                selectionId,
                "perf",
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
                SourceConnectionId,
                snapshotId,
                "dbo",
                rootTable,
                primaryKey,
                ["id"]
            );
        return selectionId;
    }

    public async Task<Guid> SavePlanAsync(Guid selectionId)
    {
        var planId = Guid.NewGuid();
        await Application.SavePlanAsync(
            planId,
            new SavePlanRequest("perf plan", null, "\"0\"", selectionId, SourceConnectionId, TargetConnectionId),
            CancellationToken.None
        );
        return planId;
    }

    public Task SealAsync(Guid planId) => Application.QueuePlanSealAsync(planId, CancellationToken.None);

    public async Task<TransferJob> TransferAsync(Guid planId, TimeSpan timeout)
    {
        await _worker.StartAsync(CancellationToken.None);
        try
        {
            var started = DateTime.UtcNow;
            var receipt = await Application.StartJobAsync(planId, Guid.NewGuid().ToString("N"), CancellationToken.None);
            var jobs = _provider.GetRequiredService<JobStore>();
            var jobId = receipt.JobId ?? throw new InvalidOperationException("Job start returned no job id.");
            var deadline = DateTime.UtcNow + timeout + TimeSpan.FromSeconds(30);
            TransferJob job;
            while (true)
            {
                job = jobs.Get(jobId);
                if (
                    job.State
                    is JobState.Succeeded
                        or JobState.Failed
                        or JobState.Cancelled
                        or JobState.VerificationFailed
                )
                    break;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Transfer did not finish; last state " + job.State + ".");
                await Task.Delay(50);
            }
            TransferDuration = DateTime.UtcNow - started;
            var events = await _provider
                .GetRequiredService<IJobEventReader>()
                .ReadAfterAsync(jobId, null, CancellationToken.None);
            RowsWritten = events.Events.Count == 0 ? 0 : events.Events[^1].Payload.RowsTransferred;
            if (job.State is not JobState.Succeeded)
                throw new InvalidOperationException("Transfer ended " + job.State + ": " + job.FailureDetail);
            return job;
        }
        finally
        {
            await _worker.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        Directory.Delete(_directory, true);
    }
}
