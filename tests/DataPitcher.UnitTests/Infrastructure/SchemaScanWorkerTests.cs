using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Schema;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Schema;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class SchemaScanWorkerTests
{
    [Fact]
    public async Task ProcessNextAsync_QueuesProcessesAndProjectsAnImmutableSnapshot()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-01", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var introspector = new BlockingIntrospector(Content());
        var worker = new SchemaScanWorker(snapshots, profiles, new Resolver(), new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(introspector) }));

        var queued = await snapshots.QueueAsync(profile.ConnectionId, "scan-01", CancellationToken.None);
        var replayed = await snapshots.QueueAsync(profile.ConnectionId, "scan-01", CancellationToken.None);
        var processing = worker.ProcessNextAsync(CancellationToken.None);
        await introspector.Started;
        var running = await snapshots.GetScanAsync(profile.ConnectionId, queued.ScanId, CancellationToken.None);
        introspector.Release();
        await processing;
        var completed = await snapshots.GetScanAsync(profile.ConnectionId, queued.ScanId, CancellationToken.None);
        var snapshot = await snapshots.GetAsync(profile.ConnectionId, completed.SnapshotId!.Value, CancellationToken.None);
        var graph = await snapshots.GetGraphAsync(profile.ConnectionId, snapshot.SnapshotId, CancellationToken.None);
        var table = await snapshots.GetTableAsync(profile.ConnectionId, snapshot.SnapshotId, "app", "Orders", CancellationToken.None);
        var neighbourhood = await snapshots.GetNeighbourhoodAsync(profile.ConnectionId, snapshot.SnapshotId, "app", "Orders", 1, CancellationToken.None);

        Assert.Equal(queued.ScanId, replayed.ScanId);
        Assert.Equal(SchemaScanState.Queued, queued.State);
        Assert.Equal(SchemaScanState.Running, running.State);
        Assert.Equal(SchemaScanState.Completed, completed.State);
        Assert.NotEqual(Guid.Empty, completed.SnapshotId);
        Assert.Equal(CanonicalSchemaSnapshotHasher.Hash(Content()), snapshot.Hash);
        Assert.Single(graph.Edges, edge => edge.Child == new SchemaTableAddress("app", "Orders") && edge.Parent == new SchemaTableAddress("app", "Customers"));
        Assert.Equal("Orders", table.Table.Name);
        Assert.Equal(1, neighbourhood.Depth);
        Assert.Contains(neighbourhood.Tables, item => item == new SchemaTableAddress("app", "Customers"));
        Assert.Throws<NotSupportedException>(() => ((IList<SchemaTable>)snapshot.Content.Tables).Add(Orders()));
    }

    [Fact]
    public async Task ProcessNextAsync_WhenScanIsReplayedOrMetadataChanges_ReusesOrCreatesTheExpectedSnapshot()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-02", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var introspector = new BlockingIntrospector(Content());
        var resolver = new Resolver();
        var worker = new SchemaScanWorker(snapshots, profiles, resolver, new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(introspector) }));

        var first = await snapshots.QueueAsync(profile.ConnectionId, "scan-02", CancellationToken.None);
        var firstProcessing = worker.ProcessNextAsync(CancellationToken.None);
        await introspector.Started;
        introspector.Release();
        await firstProcessing;
        var firstCompleted = await snapshots.GetScanAsync(profile.ConnectionId, first.ScanId, CancellationToken.None);
        var resolverCalls = resolver.Calls;
        var introspectorCalls = introspector.Calls;
        var replayed = await snapshots.QueueAsync(profile.ConnectionId, "scan-02", CancellationToken.None);
        await worker.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(first.ScanId, replayed.ScanId);
        Assert.Equal(resolverCalls, resolver.Calls);
        Assert.Equal(introspectorCalls, introspector.Calls);

        introspector.Content = new SchemaSnapshotContent(new[] { Orders("OrderNumber"), Customers() }, new[] { ForeignKey() });
        var second = await snapshots.QueueAsync(profile.ConnectionId, "scan-03", CancellationToken.None);
        var secondProcessing = worker.ProcessNextAsync(CancellationToken.None);
        await introspector.StartedAgain;
        introspector.ReleaseAgain();
        await secondProcessing;
        var secondCompleted = await snapshots.GetScanAsync(profile.ConnectionId, second.ScanId, CancellationToken.None);

        Assert.NotEqual(firstCompleted.SnapshotHash, secondCompleted.SnapshotHash);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenResolutionOrIntrospectionFails_StoresOnlyFixedFailureCodes()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-03", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var worker = new SchemaScanWorker(snapshots, profiles, new Resolver(), new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ThrowingIntrospector()) }));
        var scan = await snapshots.QueueAsync(profile.ConnectionId, "scan-04", CancellationToken.None);

        await worker.ProcessNextAsync(CancellationToken.None);
        var failed = await snapshots.GetScanAsync(profile.ConnectionId, scan.ScanId, CancellationToken.None);

        Assert.Equal(SchemaScanState.Failed, failed.State);
        Assert.Equal("schema_scan_failed", failed.FailureCode);
        Assert.DoesNotContain("scan-secret-sentinel", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenTheProviderIsNotRegistered_StoresTheUnsupportedProviderFailure()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(new ConnectionProfileDraft("Source", "unregistered", new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_SCAN_SECRET"), "app", "__datapitcher"), "profile-unsupported-provider", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var scan = await snapshots.QueueAsync(profile.ConnectionId, "scan-unsupported-provider", CancellationToken.None);
        var worker = new SchemaScanWorker(snapshots, profiles, new Resolver(), new ConnectionProviderRegistry(Array.Empty<IConnectionProvider>()));

        await worker.ProcessNextAsync(CancellationToken.None);

        var failed = await snapshots.GetScanAsync(profile.ConnectionId, scan.ScanId, CancellationToken.None);
        Assert.Equal(SchemaScanState.Failed, failed.State);
        Assert.Equal("unsupported_provider", failed.FailureCode);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenSecretResolutionFails_StoresTheFixedConnectionFailure()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-resolution-failure", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var scan = await snapshots.QueueAsync(profile.ConnectionId, "scan-resolution-failure", CancellationToken.None);
        var worker = new SchemaScanWorker(snapshots, profiles, new ThrowingResolver(), new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ThrowingIntrospector()) }));

        await worker.ProcessNextAsync(CancellationToken.None);

        var failed = await snapshots.GetScanAsync(profile.ConnectionId, scan.ScanId, CancellationToken.None);
        Assert.Equal(SchemaScanState.Failed, failed.State);
        Assert.Equal("connection_failed", failed.FailureCode);
    }

    [Fact]
    public async Task ProcessNextAsync_WhenPersistedProfileIsInvalid_StoresTheFixedConnectionFailure()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-invalid", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var scan = await snapshots.QueueAsync(profile.ConnectionId, "scan-invalid-profile", CancellationToken.None);
        using (var db = fixture.Database.Open()) db.Execute("UPDATE ConnectionProfiles SET SecretReferenceKind = 'invalid' WHERE ConnectionId = @connectionId", new DataParameter("connectionId", profile.ConnectionId.ToString()));
        var worker = new SchemaScanWorker(snapshots, profiles, new Resolver(), new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ThrowingIntrospector()) }));

        await worker.ProcessNextAsync(CancellationToken.None);

        var failed = await snapshots.GetScanAsync(profile.ConnectionId, scan.ScanId, CancellationToken.None);
        Assert.Equal(SchemaScanState.Failed, failed.State);
        Assert.Equal("connection_failed", failed.FailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStoppedBeforePolling_LeavesQueuedScansUntouched()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-stopped-worker", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var queued = await snapshots.QueueAsync(profile.ConnectionId, "scan-stopped-worker", CancellationToken.None);
        var worker = new SchemaScanWorker(snapshots, profiles, new Resolver(), new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ThrowingIntrospector()) }));
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        await ExecuteAsync(worker, stopped.Token);

        Assert.Equal(SchemaScanState.Queued, (await snapshots.GetScanAsync(profile.ConnectionId, queued.ScanId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunning_PollsAndCompletesQueuedScansUntilStopped()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-running-worker", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var queued = await snapshots.QueueAsync(profile.ConnectionId, "scan-running-worker", CancellationToken.None);
        var resolver = new Resolver();
        var worker = new SchemaScanWorker(snapshots, profiles, resolver, new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ImmediateIntrospector(Content())) }));

        await worker.StartAsync(CancellationToken.None);
        await WaitForStateAsync(snapshots, profile.ConnectionId, queued.ScanId, SchemaScanState.Completed);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SchemaScanState.Completed, (await snapshots.GetScanAsync(profile.ConnectionId, queued.ScanId, CancellationToken.None)).State);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task GetScanAsync_WhenTheScanDoesNotBelongToTheConnection_FailsWithTheFixedNotFoundError()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.GetScanAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("Schema scan was not found.", exception.Message);
    }

    [Fact]
    public async Task GetAsync_WhenTheSnapshotDoesNotBelongToTheConnection_FailsWithTheFixedNotFoundError()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-missing-snapshot", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.GetAsync(profile.ConnectionId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("Schema snapshot was not found.", exception.Message);
    }

    [Fact]
    public async Task GetAsync_WhenPersistedSnapshotContentIsNull_FailsWithTheFixedInvalidSnapshotError()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-invalid-snapshot", CancellationToken.None);
        var snapshotId = Guid.NewGuid();
        using (var db = fixture.Database.Open())
            db.Execute("INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)", new DataParameter[] { new("snapshotId", snapshotId.ToString()), new("connectionId", profile.ConnectionId.ToString()), new("snapshotHash", "invalid"), new("contentJson", "null"), new("createdUtc", fixture.Clock.UtcNow.ToString("O")) });
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.GetAsync(profile.ConnectionId, snapshotId, CancellationToken.None));

        Assert.Equal("Schema snapshot is invalid.", exception.Message);
    }

    [Fact]
    public async Task GetNeighbourhoodAsync_WhenDepthIsZero_RejectsTheInvalidProjectionRequest()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-zero-depth", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var snapshot = await StoreSnapshotAsync(snapshots, profile, Content());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => snapshots.GetNeighbourhoodAsync(profile.ConnectionId, snapshot.SnapshotId, "app", "Orders", 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetNeighbourhoodAsync_WhenStartingAtTheParent_IncludesTheReferencingChild()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-parent-neighbourhood", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var snapshot = await StoreSnapshotAsync(snapshots, profile, Content());

        var neighbourhood = await snapshots.GetNeighbourhoodAsync(profile.ConnectionId, snapshot.SnapshotId, "app", "Customers", 1, CancellationToken.None);

        Assert.Contains(neighbourhood.Tables, table => table == new SchemaTableAddress("app", "Orders"));
        Assert.Single(neighbourhood.Edges, edge => edge.Child == new SchemaTableAddress("app", "Orders") && edge.Parent == new SchemaTableAddress("app", "Customers"));
    }

    [Fact]
    public async Task GetNeighbourhoodAsync_WhenTheStartingTableIsMissing_FailsWithTheFixedNotFoundError()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-missing-neighbourhood", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var snapshot = await StoreSnapshotAsync(snapshots, profile, Content());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.GetNeighbourhoodAsync(profile.ConnectionId, snapshot.SnapshotId, "missing", "Missing", 1, CancellationToken.None));

        Assert.Equal("Schema table was not found.", exception.Message);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenTheClaimIsLost_ReturnsNoClaimAndLeavesTheScanQueued()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-lost-claim", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var queued = await snapshots.QueueAsync(profile.ConnectionId, "scan-lost-claim", CancellationToken.None);
        using (var db = fixture.Database.Open()) db.Execute("CREATE TRIGGER lose_schema_claim BEFORE UPDATE OF State ON SchemaScans WHEN NEW.State = 'Running' BEGIN SELECT RAISE(IGNORE); END;");

        var claimed = await snapshots.ClaimNextAsync(CancellationToken.None);

        Assert.Null(claimed);
        Assert.Equal(SchemaScanState.Queued, (await snapshots.GetScanAsync(profile.ConnectionId, queued.ScanId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task GetTableAsync_WhenTheTableHasNoPrimaryKey_PreservesTheAbsentKey()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await profiles.CreateAsync(Draft(), "profile-keyless-table", CancellationToken.None);
        var snapshots = new SchemaSnapshotStore(fixture.Database, fixture.Clock);
        var content = new SchemaSnapshotContent(new[] { new SchemaTable("app", "Audit", new[] { new SchemaColumn("Message", "text", "System.String", true) }, null, Array.Empty<SchemaKey>()) }, Array.Empty<SchemaForeignKey>());
        var snapshot = await StoreSnapshotAsync(snapshots, profile, content);

        var table = await snapshots.GetTableAsync(profile.ConnectionId, snapshot.SnapshotId, "app", "Audit", CancellationToken.None);

        Assert.Null(table.Table.PrimaryKey);
    }

    private static ConnectionProfileDraft Draft() => new("Source", "postgresql", new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_SCAN_SECRET"), "app", "__datapitcher");
    private static async Task<StoredSchemaSnapshot> StoreSnapshotAsync(SchemaSnapshotStore snapshots, ConnectionProfile profile, SchemaSnapshotContent content)
    {
        _ = await snapshots.QueueAsync(profile.ConnectionId, Guid.NewGuid().ToString("N"), CancellationToken.None);
        var scan = await snapshots.ClaimNextAsync(CancellationToken.None) ?? throw new InvalidOperationException("Schema scan was not claimed.");
        await snapshots.CompleteAsync(scan, content, CancellationToken.None);
        var completed = await snapshots.GetScanAsync(profile.ConnectionId, scan.ScanId, CancellationToken.None);
        return await snapshots.GetAsync(profile.ConnectionId, completed.SnapshotId!.Value, CancellationToken.None);
    }
    private static Task ExecuteAsync(SchemaScanWorker worker, CancellationToken cancellationToken) => (Task)(typeof(SchemaScanWorker).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(worker, [cancellationToken]) ?? throw new InvalidOperationException("Schema scan worker did not start."));
    private static async Task WaitForStateAsync(SchemaSnapshotStore snapshots, Guid connectionId, Guid scanId, SchemaScanState expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await snapshots.GetScanAsync(connectionId, scanId, CancellationToken.None)).State == expected) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Schema scan did not reach the expected state.");
    }
    private static SchemaSnapshotContent Content() => new(new[] { Orders(), Customers() }, new[] { ForeignKey() });
    private static SchemaTable Orders(string column = "CustomerId") => new("app", "Orders", new[] { new SchemaColumn("Id", "int", "System.Int32", false), new SchemaColumn(column, "int", "System.Int32", false) }, new SchemaKey("PK_Orders", new[] { "Id" }), Array.Empty<SchemaKey>());
    private static SchemaTable Customers() => new("app", "Customers", new[] { new SchemaColumn("Id", "int", "System.Int32", false) }, new SchemaKey("PK_Customers", new[] { "Id" }), Array.Empty<SchemaKey>());
    private static SchemaForeignKey ForeignKey() => new("FK_Orders_Customers", new SchemaTableAddress("app", "Orders"), new SchemaTableAddress("app", "Customers"), new[] { "CustomerId" }, new[] { "Id" }, true, true);

    private sealed class Resolver : ISecretReferenceResolver
    {
        public int Calls { get; private set; }
        public Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) { Calls++; return Task.FromResult("resolved-secret"); }
    }

    private sealed class ThrowingResolver : ISecretReferenceResolver
    {
        public Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromException<string>(new InvalidOperationException("secret unavailable"));
    }

    private sealed class Provider(ISchemaIntrospector introspector) : IConnectionProvider
    {
        public string ProviderId => "postgresql";
        public ICapabilityDetector CapabilityDetector => throw new NotSupportedException();
        public ISchemaIntrospector SchemaIntrospector { get; } = introspector;
    }

    private sealed class BlockingIntrospector(SchemaSnapshotContent content) : ISchemaIntrospector
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _startedAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releasedAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public SchemaSnapshotContent Content { get; set; } = content;
        public Task Started => _started.Task;
        public Task StartedAgain => _startedAgain.Task;
        public void Release() => _released.TrySetResult(true);
        public void ReleaseAgain() => _releasedAgain.TrySetResult(true);
        public async Task<SchemaSnapshotContent> ReadAsync(ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) { _started.TrySetResult(true); await _released.Task.WaitAsync(cancellationToken); }
            else { _startedAgain.TrySetResult(true); await _releasedAgain.Task.WaitAsync(cancellationToken); }
            return Content;
        }
    }

    private sealed class ThrowingIntrospector : ISchemaIntrospector
    {
        public Task<SchemaSnapshotContent> ReadAsync(ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken) => Task.FromException<SchemaSnapshotContent>(new InvalidOperationException("scan-secret-sentinel"));
    }

    private sealed class ImmediateIntrospector(SchemaSnapshotContent content) : ISchemaIntrospector
    {
        public Task<SchemaSnapshotContent> ReadAsync(ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken) => Task.FromResult(content);
    }
}
