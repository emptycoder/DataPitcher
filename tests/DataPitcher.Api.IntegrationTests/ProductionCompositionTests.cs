using System.Globalization;
using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Events;
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using DataPitcher.Infrastructure.Selections;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
using LinqToDB.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void AddDataPitcherComposition_RegistersTheProductionServicesAndAppliesMigrations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"datapitcher-composition-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDataPitcherComposition(Configuration(databasePath, Path.GetTempPath()));
            using var provider = services.BuildServiceProvider();

            provider.ApplyControlDatabaseMigrations();

            Assert.IsType<DataPitcherApplication>(provider.GetRequiredService<IDataPitcherApplication>());
            Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
            Assert.IsType<SecretReferenceResolver>(provider.GetRequiredService<ISecretReferenceResolver>());
            Assert.IsType<SchemaScanWorker>(Assert.Single(provider.GetServices<IHostedService>()));
            var registry = provider.GetRequiredService<IConnectionProviderRegistry>();
            Assert.IsType<PostgreSqlConnectionProvider>(registry.Get("postgresql"));
            Assert.IsType<SqlServerConnectionProvider>(registry.Get("sqlserver"));
            Assert.Same(provider.GetRequiredService<IJobEventWriter>(), provider.GetRequiredService<IJobEventReader>());
            using var database = provider.GetRequiredService<ControlDatabase>().Open();
            Assert.NotEmpty(database.Query<int>("SELECT Version FROM SchemaVersion"));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void AddDataPitcherComposition_RequiresTheControlDatabasePath()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherComposition(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void AddDataPitcherComposition_RequiresTheSecretsRoot()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControlDatabase:Path"] = Path.Combine(Path.GetTempPath(), $"datapitcher-missing-secret-{Guid.NewGuid():N}.db"),
        }).Build();

        Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherComposition(configuration));
    }

    [Fact]
    public async Task DevelopmentAccessDefaults_GrantTheAuthenticatedResourceAndReadJwtExpiry()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("exp", "0")]));
        var grants = new DevelopmentResourceAccessGrantReader();

        Assert.True(await grants.IsGrantedAsync(principal, new ConnectionResource(Guid.NewGuid()), CancellationToken.None));
        Assert.Equal(DateTimeOffset.UnixEpoch, new DevelopmentValidatedAccessTokenLifetime().GetExpiryUtc(principal));
    }

    [Fact]
    public void DevelopmentAccessDefaults_UseAShortExpiryWhenTheExpiryClaimIsInvalid()
    {
        var before = DateTimeOffset.UtcNow;
        var expiry = new DevelopmentValidatedAccessTokenLifetime().GetExpiryUtc(new ClaimsPrincipal(new ClaimsIdentity([new Claim("exp", "invalid")])));
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(expiry, before.AddMinutes(5), after.AddMinutes(5));
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesConnectionAndSnapshotOperations()
    {
        using var fixture = new ProductionApplicationFixture();
        var credentialId = Guid.NewGuid();

        var created = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Source", "postgresql", credentialId, "connection-create"), CancellationToken.None);
        var connections = await fixture.Application.ListConnectionsAsync(CancellationToken.None);
        var check = await fixture.Application.QueueConnectionCheckAsync(created.ConnectionId, CancellationToken.None);
        var scan = await fixture.Application.QueueSchemaScanAsync(created.ConnectionId, CancellationToken.None);
        var snapshotId = fixture.SeedSnapshot(created.ConnectionId);
        var snapshot = await fixture.Application.GetSnapshotAsync(created.ConnectionId, snapshotId, CancellationToken.None);
        var profile = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.Single(connections, connection => connection.ConnectionId == created.ConnectionId);
        Assert.Equal("DATAPITCHER_CREDENTIAL_" + credentialId.ToString("N"), profile.SecretReference.Locator);
        Assert.Equal(created.ConnectionId, check.ConnectionId);
        Assert.Equal(created.ConnectionId, scan.ConnectionId);
        Assert.Equal("snapshot-hash", snapshot.Hash);
    }

    [Fact]
    public async Task DataPitcherApplication_ListsOnlyRequestedConnectionSnapshots()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "source-create"), CancellationToken.None);
        var other = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Other", "postgresql", Guid.NewGuid(), "other-create"), CancellationToken.None);
        var sourceSnapshotId = fixture.SeedSnapshot(source.ConnectionId);
        _ = fixture.SeedSnapshot(other.ConnectionId);

        var snapshots = await fixture.Application.ListSnapshotsAsync(source.ConnectionId, CancellationToken.None);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(sourceSnapshotId, snapshot.SnapshotId);
    }

    [Fact]
    public async Task DataPitcherApplication_DoesNotFindSnapshotFromAnotherConnection()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "source-create"), CancellationToken.None);
        var other = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Other", "postgresql", Guid.NewGuid(), "other-create"), CancellationToken.None);
        var otherSnapshotId = fixture.SeedSnapshot(other.ConnectionId);

        var snapshot = await fixture.Application.FindSnapshotAsync(source.ConnectionId, otherSnapshotId, CancellationToken.None);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task DataPitcherApplication_ProjectsSchemaSnapshotContent()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "source-create"), CancellationToken.None);
        var snapshotId = fixture.SeedSnapshot(source.ConnectionId, "{\"Tables\":[{\"Schema\":\"app\",\"Name\":\"Customers\",\"Columns\":[],\"PrimaryKey\":null,\"UniqueConstraints\":[]},{\"Schema\":\"app\",\"Name\":\"Orders\",\"Columns\":[{\"Name\":\"CustomerId\",\"StoreType\":\"integer\",\"ClrType\":\"System.Int32\",\"IsNullable\":false}],\"PrimaryKey\":{\"Name\":\"PK_Orders\",\"Columns\":[\"CustomerId\"]},\"UniqueConstraints\":[]}],\"ForeignKeys\":[{\"Name\":\"FK_Orders_Customers\",\"ChildTable\":{\"Schema\":\"app\",\"Name\":\"Orders\"},\"ParentTable\":{\"Schema\":\"app\",\"Name\":\"Customers\"},\"ChildColumns\":[\"CustomerId\"],\"ParentColumns\":[\"Id\"],\"IsEnforced\":true,\"IsTrusted\":true}],\"DatabaseIdentity\":\"database\",\"ProviderVersion\":\"version\"}");

        var snapshot = await fixture.Application.GetSnapshotAsync(source.ConnectionId, snapshotId, CancellationToken.None);
        var orders = Assert.Single(snapshot.Tables, table => table.Name == "Orders");
        var foreignKey = Assert.Single(snapshot.ForeignKeys);

        Assert.Equal("integer", Assert.Single(orders.Columns).StoreType);
        Assert.Equal("PK_Orders", orders.PrimaryKey!.Name);
        Assert.Equal("Customers", foreignKey.ParentTable.Name);
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesSelectionOperations()
    {
        using var fixture = new ProductionApplicationFixture();
        var selectionId = Guid.NewGuid();

        var saved = await fixture.Application.SaveSelectionAsync(selectionId, new SaveSelectionRequest("Selection", "{}", "ignored-on-create"), CancellationToken.None);
        var receipt = await fixture.Application.QueueSelectionEvaluationAsync(selectionId, CancellationToken.None);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.QueueSelectionEvaluationAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(selectionId, saved.SelectionId);
        Assert.Equal("queued", receipt.State);
        Assert.Equal("Selection was not found.", missing.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesPlanOperationsUntilClosureIsAvailable()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();

        var saved = await fixture.Application.SavePlanAsync(planId, new SavePlanRequest("Plan", null, "ignored-on-create"), CancellationToken.None);
        var pendingReview = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);
        fixture.SetPlanHash(planId, "plan-hash");
        var review = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);
        var seal = await fixture.Application.QueuePlanSealAsync(planId, CancellationToken.None);
        var inclusion = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.GetPlanInclusionPathAsync(planId, new InclusionPathRequest("app.Orders", "1"), CancellationToken.None));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.QueuePlanSealAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(1, saved.Version);
        Assert.Equal("", pendingReview.CanonicalHash);
        Assert.Equal("plan-hash", review.CanonicalHash);
        Assert.Equal(planId, seal.PlanId);
        Assert.Contains("computed dependency closure", inclusion.Message, StringComparison.Ordinal);
        Assert.Equal("Plan was not found.", missing.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_RefusesUnsealedPlanJobStarts()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(planId, new SavePlanRequest("Plan", null, "ignored-on-create"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PlanNotSealedException>(() => fixture.Application.StartJobAsync(planId, "job-start", CancellationToken.None));

        Assert.Equal("Plan must be sealed before starting a job.", exception.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_RefusesUnknownPlanJobStarts()
    {
        using var fixture = new ProductionApplicationFixture();

        var exception = await Assert.ThrowsAsync<PlanNotFoundException>(() => fixture.Application.StartJobAsync(Guid.NewGuid(), "job-start", CancellationToken.None));

        Assert.Equal("Plan was not found.", exception.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_ReviewIncludesThePlanSelectionAndConnections()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "source"), CancellationToken.None);
        var target = await fixture.Application.CreateConnectionAsync(new CreateConnectionRequest("Target", "postgresql", Guid.NewGuid(), "target"), CancellationToken.None);
        var selectionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        _ = await fixture.Selections.SaveAsync(selectionId, "Selection", "{}", "ignored-on-create", CancellationToken.None, source.ConnectionId, snapshotId);
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(planId, new SavePlanRequest("Plan", null, "ignored-on-create", selectionId, source.ConnectionId, target.ConnectionId), CancellationToken.None);

        var review = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);

        Assert.Equal(selectionId, review.Selection?.SelectionId);
        Assert.Equal("Selection", review.Selection?.DisplayName);
        Assert.Equal(source.ConnectionId, review.Selection?.ConnectionId);
        Assert.Equal(snapshotId, review.Selection?.SnapshotId);
        Assert.Equal(source.ConnectionId, review.Source?.ConnectionId);
        Assert.Equal(target.ConnectionId, review.Target?.ConnectionId);
    }

    [Fact]
    public async Task DataPitcherApplication_RejectsPlansWithUnknownSelections()
    {
        using var fixture = new ProductionApplicationFixture();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Application.SavePlanAsync(Guid.NewGuid(), new SavePlanRequest("Plan", null, "ignored-on-create", Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesJobOperationsAndProjectsTheLatestEvent()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(planId, new SavePlanRequest("Plan", null, "ignored-on-create"), CancellationToken.None);
        await fixture.Plans.SealAsync(planId, SealedContent(), CancellationToken.None);

        var started = await fixture.Application.StartJobAsync(planId, "job-start", CancellationToken.None);
        var initial = await fixture.Application.GetJobAsync(started.JobId!.Value, CancellationToken.None);
        await fixture.Events.AppendAsync(new JobEventAppend(started.JobId.Value, "progress", new JobEventPayload("Running", 10, 100)), CancellationToken.None);
        var current = await fixture.Application.GetJobAsync(started.JobId.Value, CancellationToken.None);
        var claim = await fixture.Jobs.TryClaimNextAsync("test-worker", TimeSpan.FromMinutes(1), CancellationToken.None) ?? throw new InvalidOperationException("Job was not claimed.");
        await fixture.Jobs.PrepareAsync(claim, CancellationToken.None);
        await fixture.Jobs.MarkRunningAsync(claim.Lease, CancellationToken.None);
        _ = await fixture.Application.QueueJobCommandAsync(started.JobId.Value, JobCommand.Pause, CancellationToken.None);
        _ = await fixture.Application.QueueJobCommandAsync(started.JobId.Value, JobCommand.Resume, CancellationToken.None);
        _ = await fixture.Application.QueueJobCommandAsync(started.JobId.Value, JobCommand.Cancel, CancellationToken.None);

        Assert.Equal(0, initial.RowsTransferred);
        Assert.Equal(10, current.RowsTransferred);
        Assert.Equal(100, current.BytesTransferred);
        Assert.Equal("Cancelling", fixture.Jobs.Get(started.JobId.Value).State.ToString());
    }

    [Fact]
    public async Task DataPitcherApplication_RejectsUnknownJobCommands()
    {
        using var fixture = new ProductionApplicationFixture();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Application.QueueJobCommandAsync(Guid.NewGuid(), (JobCommand)99, CancellationToken.None));

        Assert.Equal("command", exception.ParamName);
    }

    private static IConfiguration Configuration(string databasePath, string secretsRoot) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ControlDatabase:Path"] = databasePath,
            ["Secrets:Root"] = secretsRoot,
        }).Build();

    private static TransferPlanContent SealedContent() => new(
        new ConnectionFingerprint("postgresql", "source", "source"),
        new ConnectionFingerprint("postgresql", "target", "target"),
        new SchemaSnapshotReference("source"),
        new SchemaSnapshotReference("target"),
        [],
        [],
        [],
        ConsistencyMode.FrozenKeys,
        TransferMode.DirectFast,
        TriggerStrategy.Fire,
        ConstraintStrategy.Enforce,
        [],
        [],
        new BatchTarget(1, 1),
        VerificationStrategy.Standard,
        new ManifestCounts(0, 0, 0, 0));

    private sealed class ProductionApplicationFixture : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"datapitcher-application-{Guid.NewGuid():N}.db");

        public ProductionApplicationFixture()
        {
            Database = new ControlDatabase($"Data Source={_databasePath}");
            Clock = new ManualClock(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
            new ControlDatabaseMigrator(Database, Clock).Apply();
            Profiles = new ConnectionProfileStore(Database, Clock);
            Jobs = new JobStore(Database, Clock);
            Events = new JobEventStore(Database, Clock, new JobEventSignal());
            var snapshots = new SchemaSnapshotStore(Database, Clock);
            Selections = new SelectionStore(Database, Clock);
            Plans = new PlanStore(Database, Clock);
            Application = new DataPitcherApplication(
                Profiles,
                new ConnectionHealthService(Profiles, new SecretReferenceResolver(Path.GetTempPath()), new ConnectionProviderRegistry([new PostgreSqlConnectionProvider()])),
                snapshots,
                Selections,
                Plans,
                Jobs,
                Events);
        }

        public DataPitcherApplication Application { get; }
        public ManualClock Clock { get; }
        public ControlDatabase Database { get; }
        public JobEventStore Events { get; }
        public JobStore Jobs { get; }
        public ConnectionProfileStore Profiles { get; }
        public SelectionStore Selections { get; }
        public PlanStore Plans { get; }

        public Guid SeedSnapshot(Guid connectionId, string? content = null)
        {
            var snapshotId = Guid.NewGuid();
            using var database = Database.Open();
            database.Execute(
                "INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)",
                new DataParameter[]
                {
                    new("snapshotId", snapshotId.ToString()),
                    new("connectionId", connectionId.ToString()),
                    new("snapshotHash", "snapshot-hash"),
                    new("contentJson", content ?? "{\"Tables\":[],\"ForeignKeys\":[],\"DatabaseIdentity\":\"database\",\"ProviderVersion\":\"version\"}"),
                    new("createdUtc", Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                });
            return snapshotId;
        }

        public void SetPlanHash(Guid planId, string hash)
        {
            using var database = Database.Open();
            database.Execute("UPDATE Plans SET CanonicalHash = @hash WHERE PlanId = @planId", new DataParameter[] { new("hash", hash), new("planId", planId.ToString()) });
        }

        public void Dispose()
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
