using System.Globalization;
using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Connections;
using DataPitcher.Application.Events;
using DataPitcher.Application.Plans;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Time;
using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
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
            var hostedServices = provider.GetServices<IHostedService>();
            Assert.Contains(
                hostedServices,
                service => string.Equals(service.GetType().Name, nameof(SchemaScanWorker), StringComparison.Ordinal)
            );
            Assert.Contains(
                hostedServices,
                service => string.Equals(service.GetType().Name, nameof(JobWorker), StringComparison.Ordinal)
            );
            var registry = provider.GetRequiredService<IConnectionProviderRegistry>();
            Assert.IsType<PostgreSqlConnectionProvider>(registry.Get("postgresql"));
            Assert.IsType<SqlServerConnectionProvider>(registry.Get("sqlserver"));
            Assert.Same(provider.GetRequiredService<IJobEventWriter>(), provider.GetRequiredService<IJobEventReader>());
            using var database = provider.GetRequiredService<ControlDatabase>().Open();
            Assert.NotEmpty(database.Query<int>("SELECT Version FROM SchemaVersion"));
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public void AddDataPitcherComposition_RequiresTheControlDatabasePath()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddDataPitcherComposition(new ConfigurationBuilder().Build())
        );
    }

    [Fact]
    public void AddDataPitcherComposition_RequiresTheSecretsRoot()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ControlDatabase:Path"] = Path.Combine(
                        Path.GetTempPath(),
                        $"datapitcher-missing-secret-{Guid.NewGuid():N}.db"
                    ),
                }
            )
            .Build();

        Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherComposition(configuration));
    }

    [Fact]
    public async Task DevelopmentAccessDefaults_GrantTheAuthenticatedResourceAndReadJwtExpiry()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("exp", "0")]));
        var grants = new DevelopmentResourceAccessGrantReader();

        Assert.True(
            await grants.IsGrantedAsync(principal, new ConnectionResource(Guid.NewGuid()), CancellationToken.None)
        );
        Assert.Equal(DateTimeOffset.UnixEpoch, new DevelopmentValidatedAccessTokenLifetime().GetExpiryUtc(principal));
    }

    [Fact]
    public void DevelopmentAccessDefaults_UseAShortExpiryWhenTheExpiryClaimIsInvalid()
    {
        var before = DateTimeOffset.UtcNow;
        var expiry = new DevelopmentValidatedAccessTokenLifetime().GetExpiryUtc(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("exp", "invalid")]))
        );
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(expiry, before.AddMinutes(5), after.AddMinutes(5));
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesConnectionAndSnapshotOperations()
    {
        using var fixture = new ProductionApplicationFixture();
        var credentialId = Guid.NewGuid();

        var created = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                credentialId,
                "connection-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var connections = await fixture.Application.ListConnectionsAsync(CancellationToken.None);
        var check = await fixture.Application.QueueConnectionCheckAsync(created.ConnectionId, CancellationToken.None);
        var scan = await fixture.Application.QueueSchemaScanAsync(created.ConnectionId, CancellationToken.None);
        var snapshotId = fixture.SeedSnapshot(created.ConnectionId);
        var snapshot = await fixture.Application.GetSnapshotAsync(
            created.ConnectionId,
            snapshotId,
            CancellationToken.None
        );
        var profile = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.Single(connections, connection => connection.ConnectionId == created.ConnectionId);
        Assert.Equal(SecretReferenceKind.FileMounted, profile.SecretReference.Kind);
        Assert.Contains(credentialId.ToString("N"), profile.SecretReference.Locator);
        Assert.Equal(created.ConnectionId, check.ConnectionId);
        Assert.Equal(created.ConnectionId, scan.ConnectionId);
        Assert.Equal("snapshot-hash", snapshot.Hash);
    }

    [Fact]
    public async Task DataPitcherApplication_UpdateConnection_ReplacesSecretOnlyWhenProvided()
    {
        using var fixture = new ProductionApplicationFixture();
        var created = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "create", "Host=one;Database=app"),
            CancellationToken.None
        );
        var before = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        var renamed = await fixture.Application.UpdateConnectionAsync(
            created.ConnectionId,
            new UpdateConnectionRequest("Renamed", "postgresql", created.ETag, null),
            CancellationToken.None
        );
        var kept = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);
        var replaced = await fixture.Application.UpdateConnectionAsync(
            created.ConnectionId,
            new UpdateConnectionRequest("Renamed again", "postgresql", renamed.ETag, "Host=two;Database=app"),
            CancellationToken.None
        );
        var after = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.Equal(before.SecretReference, kept.SecretReference);
        Assert.Equal("Renamed again", replaced.DisplayName);
        Assert.NotEqual(before.SecretReference, after.SecretReference);
        Assert.False(File.Exists(before.SecretReference.Locator));
        Assert.Equal("Host=two;Database=app", await File.ReadAllTextAsync(after.SecretReference.Locator));
    }

    [Fact]
    public async Task DataPitcherApplication_CreateConnection_WithWildcardIfMatch_CreatesDistinctProfiles()
    {
        using var fixture = new ProductionApplicationFixture();

        var first = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("LocalDB", "sqlserver", Guid.NewGuid(), "*", "Server=(localdb)\\x;Database=a"),
            CancellationToken.None
        );
        var second = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Staging", "postgresql", Guid.NewGuid(), "*", "Host=two;Database=b"),
            CancellationToken.None
        );
        var connections = await fixture.Application.ListConnectionsAsync(CancellationToken.None);

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        Assert.Equal("Staging", second.DisplayName);
        Assert.Equal(["LocalDB", "Staging"], connections.Select(connection => connection.DisplayName).ToArray());
    }

    [Fact]
    public async Task DataPitcherApplication_CreateConnection_WithTheSameIdempotencyKey_ReturnsTheExistingProfile()
    {
        using var fixture = new ProductionApplicationFixture();

        var first = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "same-key", "Host=one;Database=a"),
            CancellationToken.None
        );
        var retried = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "same-key", "Host=one;Database=a"),
            CancellationToken.None
        );

        Assert.Equal(first.ConnectionId, retried.ConnectionId);
        Assert.Single(await fixture.Application.ListConnectionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DataPitcherApplication_ConnectionDetails_RedactsThePasswordAndUpdateCanKeepIt()
    {
        using var fixture = new ProductionApplicationFixture();
        var created = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "create",
                "Host=one;Database=app;Username=app;Password=\"top;secret\""
            ),
            CancellationToken.None
        );

        var details = await fixture.Application.GetConnectionDetailsAsync(created.ConnectionId, CancellationToken.None);
        var updated = await fixture.Application.UpdateConnectionAsync(
            created.ConnectionId,
            new UpdateConnectionRequest(
                "Source",
                "postgresql",
                created.ETag,
                "Host=two;Database=app;Username=app",
                KeepStoredPassword: true
            ),
            CancellationToken.None
        );
        var profile = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);
        var stored = await File.ReadAllTextAsync(profile.SecretReference.Locator);
        var replaced = await fixture.Application.UpdateConnectionAsync(
            created.ConnectionId,
            new UpdateConnectionRequest(
                "Source",
                "postgresql",
                updated.ETag,
                "Host=three;Database=app;Username=app;Password=fresh",
                KeepStoredPassword: true
            ),
            CancellationToken.None
        );
        var replacedProfile = await fixture.Profiles.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.True(details.HasPassword);
        Assert.Equal("postgresql", details.ProviderId);
        Assert.DoesNotContain("secret", details.ConnectionString);
        Assert.Contains("host=one", details.ConnectionString);
        Assert.Contains("username=app", details.ConnectionString);
        Assert.StartsWith("Host=two;Database=app;Username=app;", stored);
        Assert.Equal("top;secret", ConnectionStringSecrets.ExtractPassword(stored));
        Assert.NotEqual(created.ETag, replaced.ETag);
        Assert.Equal(
            "Host=three;Database=app;Username=app;Password=fresh",
            await File.ReadAllTextAsync(replacedProfile.SecretReference.Locator)
        );
    }

    [Fact]
    public async Task DataPitcherApplication_BusinessSchema_DefaultsPerProviderAndIsKeptUnlessSent()
    {
        using var fixture = new ProductionApplicationFixture();

        var sqlServer = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Mssql", "sqlserver", Guid.NewGuid(), "*", "Server=one;Database=a"),
            CancellationToken.None
        );
        var postgres = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Pg", "postgresql", Guid.NewGuid(), "*", "Host=one;Database=a"),
            CancellationToken.None
        );
        var custom = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Sales", "sqlserver", Guid.NewGuid(), "*", "Server=one;Database=a", " sales "),
            CancellationToken.None
        );
        var kept = await fixture.Application.UpdateConnectionAsync(
            custom.ConnectionId,
            new UpdateConnectionRequest("Sales renamed", "sqlserver", custom.ETag),
            CancellationToken.None
        );
        var afterRename = await fixture.Profiles.GetProfileAsync(custom.ConnectionId, CancellationToken.None);
        var changed = await fixture.Application.UpdateConnectionAsync(
            custom.ConnectionId,
            new UpdateConnectionRequest("Sales renamed", "sqlserver", kept.ETag, BusinessSchema: "archive"),
            CancellationToken.None
        );

        Assert.Equal(
            "dbo",
            (
                await fixture.Application.GetConnectionDetailsAsync(sqlServer.ConnectionId, CancellationToken.None)
            ).BusinessSchema
        );
        Assert.Equal(
            "public",
            (
                await fixture.Application.GetConnectionDetailsAsync(postgres.ConnectionId, CancellationToken.None)
            ).BusinessSchema
        );
        Assert.Equal("sales", afterRename.BusinessSchema);
        Assert.NotEqual(custom.ETag, kept.ETag);
        Assert.Equal(
            "archive",
            (
                await fixture.Application.GetConnectionDetailsAsync(changed.ConnectionId, CancellationToken.None)
            ).BusinessSchema
        );
    }

    [Fact]
    public async Task DataPitcherApplication_TestConnection_ReportsTheDriverFailureWithoutSecrets()
    {
        using var fixture = new ProductionApplicationFixture();

        var result = await fixture.Application.TestConnectionAsync(
            new ConnectionTestRequest(
                "postgresql",
                "Host=127.0.0.1;Port=1;Database=app;Username=u;Password=topsecret;Timeout=1"
            ),
            CancellationToken.None
        );

        Assert.False(result.Succeeded);
        Assert.Equal("Unhealthy", result.Health);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain("topsecret", result.Error);
        Assert.Contains("CanConnect", result.MissingRequired);
    }

    [Fact]
    public async Task DataPitcherApplication_DeleteSnapshot_RemovesItUnlessASelectionStillUsesIt()
    {
        using var fixture = new ProductionApplicationFixture();
        var connection = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "*", "Host=one;Database=app"),
            CancellationToken.None
        );
        var used = fixture.SeedSnapshot(connection.ConnectionId);
        var unused = fixture.SeedSnapshot(connection.ConnectionId);
        _ = await fixture.Application.SaveSelectionAsync(
            Guid.NewGuid(),
            new SaveSelectionRequest("*", "Orders", OrdersQuery() with { SnapshotId = used }),
            CancellationToken.None
        );

        await fixture.Application.DeleteSnapshotAsync(connection.ConnectionId, unused, CancellationToken.None);
        var inUse = await Assert.ThrowsAsync<SchemaSnapshotInUseException>(() =>
            fixture.Application.DeleteSnapshotAsync(connection.ConnectionId, used, CancellationToken.None)
        );
        await Assert.ThrowsAsync<SnapshotNotFoundException>(() =>
            fixture.Application.DeleteSnapshotAsync(connection.ConnectionId, unused, CancellationToken.None)
        );
        var remaining = await fixture.Application.ListSnapshotsAsync(connection.ConnectionId, CancellationToken.None);

        Assert.Equal(1, inUse.Selections);
        Assert.Single(remaining, snapshot => snapshot.SnapshotId == used);
    }

    [Fact]
    public async Task DataPitcherApplication_ListsOnlyRequestedConnectionSnapshots()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "source-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var other = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Other",
                "postgresql",
                Guid.NewGuid(),
                "other-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
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
        var source = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "source-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var other = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Other",
                "postgresql",
                Guid.NewGuid(),
                "other-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var otherSnapshotId = fixture.SeedSnapshot(other.ConnectionId);

        var snapshot = await fixture.Application.FindSnapshotAsync(
            source.ConnectionId,
            otherSnapshotId,
            CancellationToken.None
        );

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task DataPitcherApplication_ProjectsSchemaSnapshotContent()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "source-create",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var snapshotId = fixture.SeedSnapshot(
            source.ConnectionId,
            "{\"Tables\":[{\"Schema\":\"app\",\"Name\":\"Customers\",\"Columns\":[],\"PrimaryKey\":null,\"UniqueConstraints\":[]},{\"Schema\":\"app\",\"Name\":\"Orders\",\"Columns\":[{\"Name\":\"CustomerId\",\"StoreType\":\"integer\",\"ClrType\":\"System.Int32\",\"IsNullable\":false}],\"PrimaryKey\":{\"Name\":\"PK_Orders\",\"Columns\":[\"CustomerId\"]},\"UniqueConstraints\":[]}],\"ForeignKeys\":[{\"Name\":\"FK_Orders_Customers\",\"ChildTable\":{\"Schema\":\"app\",\"Name\":\"Orders\"},\"ParentTable\":{\"Schema\":\"app\",\"Name\":\"Customers\"},\"ChildColumns\":[\"CustomerId\"],\"ParentColumns\":[\"Id\"],\"IsEnforced\":true,\"IsTrusted\":true}],\"DatabaseIdentity\":\"database\",\"ProviderVersion\":\"version\"}"
        );

        var snapshot = await fixture.Application.GetSnapshotAsync(
            source.ConnectionId,
            snapshotId,
            CancellationToken.None
        );
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

        var saved = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest("ignored-on-create", "Selection", OrdersQuery()),
            CancellationToken.None
        );
        var receipt = await fixture.Application.QueueSelectionEvaluationAsync(selectionId, CancellationToken.None);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Application.QueueSelectionEvaluationAsync(Guid.NewGuid(), CancellationToken.None)
        );

        Assert.Equal(selectionId, saved.SelectionId);
        Assert.Equal("unknown", receipt.State);
        Assert.Equal("Selection was not found.", missing.Message);
    }

    private static SelectionRequestBody OrdersQuery(string sql = "SELECT Id AS __datapitcher_key_0 FROM app.Orders") =>
        new(
            "raw",
            null,
            sql,
            [],
            "rev-1",
            RootSchema: "app",
            RootTable: "Orders",
            StableKeyConstraintName: "PK_Orders",
            StableKeyColumns: ["Id"]
        );

    [Fact]
    public async Task DataPitcherApplication_SaveSelection_PartialUpdatesKeepWhatWasNotSent()
    {
        using var fixture = new ProductionApplicationFixture();
        var selectionId = Guid.NewGuid();
        var created = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest("*", "Orders", OrdersQuery()),
            CancellationToken.None
        );

        var renamed = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest(created.ETag, "Orders for review"),
            CancellationToken.None
        );
        var afterRename = await fixture.Application.GetSelectionDetailsAsync(selectionId, CancellationToken.None);
        var unchanged = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest(renamed.ETag, "Orders for review"),
            CancellationToken.None
        );
        var requeried = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest(
                unchanged.ETag,
                Query: OrdersQuery("SELECT Id AS __datapitcher_key_0 FROM app.Orders WHERE Id > 10")
            ),
            CancellationToken.None
        );
        var afterQuery = await fixture.Application.GetSelectionDetailsAsync(selectionId, CancellationToken.None);

        Assert.Equal(2, renamed.Version);
        Assert.Equal("Orders for review", afterRename.DisplayName);
        Assert.Equal("app", afterRename.RootSchema);
        Assert.Equal("Orders", afterRename.RootTable);
        Assert.Equal(["Id"], afterRename.StableKeyColumns);
        Assert.Equal("SELECT Id AS __datapitcher_key_0 FROM app.Orders", afterRename.Query.RawSql);
        Assert.Equal(2, unchanged.Version);
        Assert.Equal(3, requeried.Version);
        Assert.Equal("Orders for review", afterQuery.DisplayName);
        Assert.Contains("WHERE Id > 10", afterQuery.Query.RawSql);
        Assert.Equal("raw", afterQuery.Mode);
    }

    [Fact]
    public async Task DataPitcherApplication_SaveSelection_RequiresAQueryToCreate()
    {
        using var fixture = new ProductionApplicationFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Application.SaveSelectionAsync(
                Guid.NewGuid(),
                new SaveSelectionRequest("*", "Name only"),
                CancellationToken.None
            )
        );
        await Assert.ThrowsAsync<SelectionNotFoundException>(() =>
            fixture.Application.GetSelectionDetailsAsync(Guid.NewGuid(), CancellationToken.None)
        );
    }

    [Fact]
    public async Task DataPitcherApplication_SavePlan_PartialUpdatesKeepWhatWasNotSentAndNoOpsKeepTheSeal()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        _ = await fixture.Application.SaveSelectionAsync(
            selectionId,
            new SaveSelectionRequest("*", "Orders", OrdersQuery()),
            CancellationToken.None
        );
        var source = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest("Source", "postgresql", Guid.NewGuid(), "*", "Host=one;Database=app"),
            CancellationToken.None
        );
        var created = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", "first note", "*", selectionId, source.ConnectionId, source.ConnectionId),
            CancellationToken.None
        );
        fixture.SetPlanHash(planId, "plan-hash");

        var noop = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest(null, null, created.ETag),
            CancellationToken.None
        );
        var afterNoop = await fixture.Application.GetPlanDetailsAsync(planId, CancellationToken.None);
        var noteOnly = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest(null, "second note", noop.ETag),
            CancellationToken.None
        );
        var afterNote = await fixture.Application.GetPlanDetailsAsync(planId, CancellationToken.None);
        var cleared = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Renamed", "", noteOnly.ETag),
            CancellationToken.None
        );
        var afterClear = await fixture.Application.GetPlanDetailsAsync(planId, CancellationToken.None);

        Assert.Equal(created.Version, noop.Version);
        Assert.Equal("plan-hash", noop.CanonicalHash);
        Assert.True(afterNoop.Sealed);
        Assert.Equal("first note", afterNoop.OperatorNote);
        Assert.Equal(created.Version + 1, noteOnly.Version);
        Assert.False(afterNote.Sealed);
        Assert.Equal("Plan", afterNote.DisplayName);
        Assert.Equal("second note", afterNote.OperatorNote);
        Assert.Equal(selectionId, afterNote.SelectionId);
        Assert.Equal(source.ConnectionId, afterNote.SourceConnectionId);
        Assert.Equal(source.ConnectionId, afterNote.TargetConnectionId);
        Assert.Equal("Renamed", afterClear.DisplayName);
        Assert.Null(afterClear.OperatorNote);
        Assert.Equal(cleared.Version, afterClear.Version);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Application.SavePlanAsync(
                Guid.NewGuid(),
                new SavePlanRequest(null, "note without a name", "*"),
                CancellationToken.None
            )
        );
        await Assert.ThrowsAsync<PlanNotFoundException>(() =>
            fixture.Application.GetPlanDetailsAsync(Guid.NewGuid(), CancellationToken.None)
        );
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesPlanOperationsUntilClosureIsAvailable()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();

        var saved = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        var pendingReview = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);
        fixture.SetPlanHash(planId, "plan-hash");
        var review = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);
        var seal = await fixture.Application.QueuePlanSealAsync(planId, CancellationToken.None);
        var inclusion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Application.GetPlanInclusionPathAsync(
                planId,
                new InclusionPathRequest("app.Orders", "1"),
                CancellationToken.None
            )
        );
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Application.QueuePlanSealAsync(Guid.NewGuid(), CancellationToken.None)
        );

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
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );

        var exception = await Assert.ThrowsAsync<PlanNotSealedException>(() =>
            fixture.Application.StartJobAsync(planId, "job-start", CancellationToken.None)
        );

        Assert.Equal("Plan must be sealed before starting a job.", exception.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_RefusesUnknownPlanJobStarts()
    {
        using var fixture = new ProductionApplicationFixture();

        var exception = await Assert.ThrowsAsync<PlanNotFoundException>(() =>
            fixture.Application.StartJobAsync(Guid.NewGuid(), "job-start", CancellationToken.None)
        );

        Assert.Equal("Plan was not found.", exception.Message);
    }

    [Fact]
    public async Task DataPitcherApplication_ReviewIncludesThePlanSelectionAndConnections()
    {
        using var fixture = new ProductionApplicationFixture();
        var source = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "source",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var target = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Target",
                "postgresql",
                Guid.NewGuid(),
                "target",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var selectionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        _ = await fixture.Selections.SaveAsync(
            selectionId,
            "Selection",
            "{}",
            "ignored-on-create",
            CancellationToken.None,
            source.ConnectionId,
            snapshotId
        );
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest(
                "Plan",
                null,
                "ignored-on-create",
                selectionId,
                source.ConnectionId,
                target.ConnectionId
            ),
            CancellationToken.None
        );

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

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Application.SavePlanAsync(
                Guid.NewGuid(),
                new SavePlanRequest("Plan", null, "ignored-on-create", Guid.NewGuid()),
                CancellationToken.None
            )
        );

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task DataPitcherApplication_DelegatesJobOperationsAndProjectsTheLatestEvent()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        await fixture.Plans.SealAsync(planId, SealedContent(), CancellationToken.None);

        var started = await fixture.Application.StartJobAsync(planId, "job-start", CancellationToken.None);
        var initial = await fixture.Application.GetJobAsync(started.JobId!.Value, CancellationToken.None);
        await fixture.Events.AppendAsync(
            new JobEventAppend(started.JobId.Value, "progress", new JobEventPayload("Running", 10, 100)),
            CancellationToken.None
        );
        var current = await fixture.Application.GetJobAsync(started.JobId.Value, CancellationToken.None);
        var claim =
            await fixture.Jobs.TryClaimNextAsync("test-worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            ?? throw new InvalidOperationException("Job was not claimed.");
        await fixture.Jobs.PrepareAsync(claim, CancellationToken.None);
        await fixture.Jobs.MarkRunningAsync(claim.Lease, CancellationToken.None);
        _ = await fixture.Application.QueueJobCommandAsync(
            started.JobId.Value,
            JobCommand.Pause,
            CancellationToken.None
        );
        _ = await fixture.Application.QueueJobCommandAsync(
            started.JobId.Value,
            JobCommand.Resume,
            CancellationToken.None
        );
        _ = await fixture.Application.QueueJobCommandAsync(
            started.JobId.Value,
            JobCommand.Cancel,
            CancellationToken.None
        );

        Assert.Equal(0, initial.RowsTransferred);
        Assert.Equal(10, current.RowsTransferred);
        Assert.Equal(100, current.BytesTransferred);
        Assert.Equal("Cancelling", fixture.Jobs.Get(started.JobId.Value).State.ToString());
    }

    [Theory]
    [InlineData("Queued", false, false, null)]
    [InlineData("Completed", true, false, null)]
    [InlineData("Failed", true, true, "schema_scan_failed")]
    public async Task DataPitcherApplication_ProjectsPersistedSchemaScanStatus(
        string state,
        bool finished,
        bool failed,
        string? failureCode
    )
    {
        using var fixture = new ProductionApplicationFixture();
        var connection = await fixture.Application.CreateConnectionAsync(
            new CreateConnectionRequest(
                "Source",
                "postgresql",
                Guid.NewGuid(),
                "status-connection",
                "Host=localhost;Database=app;Username=app;Password=x"
            ),
            CancellationToken.None
        );
        var receipt = await fixture.Application.QueueSchemaScanAsync(connection.ConnectionId, CancellationToken.None);
        var snapshotId = Guid.NewGuid();
        using (var database = fixture.Database.Open())
            database.Execute(
                "UPDATE SchemaScans SET State = @state, SnapshotId = @snapshotId, FailureCode = @failureCode WHERE ScanId = @scanId",
                new ControlParameter[]
                {
                    new("state", state),
                    new(
                        "snapshotId",
                        string.Equals(state, "Completed", StringComparison.Ordinal) ? snapshotId.ToString() : null
                    ),
                    new("failureCode", failureCode),
                    new("scanId", receipt.OperationId.ToString()),
                }
            );

        var status = await fixture.Application.GetOperationStatusAsync(receipt.OperationId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(receipt.OperationId, status!.OperationId);
        Assert.Equal("schema-scan", status.Operation);
        Assert.Equal(state, status.State);
        Assert.Equal(finished, status.Finished);
        Assert.Equal(failed, status.Failed);
        Assert.Equal(failureCode, status.FailureCode);
        Assert.Equal(
            string.Equals(state, "Completed", StringComparison.Ordinal) ? snapshotId : null,
            status.SnapshotId
        );
        Assert.Equal("/api/operations/" + receipt.OperationId, receipt.StatusUri.AbsolutePath);
    }

    [Theory]
    [InlineData("Queued", false, false, null)]
    [InlineData("Cancelled", true, false, null)]
    [InlineData("Succeeded", true, false, null)]
    [InlineData("Failed", true, true, "worker_failed")]
    [InlineData("VerificationFailed", true, true, "verification_failed")]
    public async Task DataPitcherApplication_ProjectsPersistedJobStatus(
        string state,
        bool finished,
        bool failed,
        string? failureCode
    )
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        await fixture.Plans.SealAsync(planId, SealedContent(), CancellationToken.None);
        var receipt = await fixture.Application.StartJobAsync(planId, "status-job-" + state, CancellationToken.None);
        using (var database = fixture.Database.Open())
            database.Execute(
                "UPDATE Jobs SET State = @state, FailureCode = @failureCode WHERE JobId = @jobId",
                new ControlParameter[]
                {
                    new("state", state),
                    new("failureCode", failureCode),
                    new("jobId", receipt.OperationId.ToString()),
                }
            );

        var status = await fixture.Application.GetOperationStatusAsync(receipt.OperationId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(receipt.OperationId, status!.OperationId);
        Assert.Equal("job", status.Operation);
        Assert.Equal(state, status.State);
        Assert.Equal(finished, status.Finished);
        Assert.Equal(failed, status.Failed);
        Assert.Equal(failureCode, status.FailureCode);
        Assert.Equal(receipt.JobId, status.JobId);
        Assert.Equal("/api/operations/" + receipt.OperationId, receipt.StatusUri.AbsolutePath);
    }

    [Fact]
    public async Task DataPitcherApplication_WhenOperationIsUnknown_ReturnsNull()
    {
        using var fixture = new ProductionApplicationFixture();

        var status = await fixture.Application.GetOperationStatusAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(status);
    }

    [Fact]
    public async Task DataPitcherApplication_ReturnsUnknownReceiptForUnpersistedPlanSealing()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "plan-create"),
            CancellationToken.None
        );

        var receipt = await fixture.Application.QueuePlanSealAsync(planId, CancellationToken.None);
        var status = await fixture.Application.GetOperationStatusAsync(receipt.OperationId, CancellationToken.None);

        Assert.Equal("unknown", receipt.State);
        Assert.Null(status);
    }

    [Fact]
    public async Task DataPitcherApplication_ListsStartedJobs()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        await fixture.Plans.SealAsync(planId, SealedContent(), CancellationToken.None);
        var started = await fixture.Application.StartJobAsync(planId, "list-job", CancellationToken.None);

        var jobs = await fixture.Application.ListJobsAsync(CancellationToken.None);

        var job = Assert.Single(jobs);
        Assert.Equal(started.JobId, job.JobId);
        Assert.Equal("Queued", job.State);
        Assert.Equal(fixture.Clock.UtcNow, job.CreatedUtc);
        Assert.Equal(fixture.Clock.UtcNow, job.UpdatedUtc);
        Assert.Equal(0, job.RowsTransferred);
    }

    [Fact]
    public async Task DataPitcherApplication_RejectsUnknownJobCommands()
    {
        using var fixture = new ProductionApplicationFixture();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Application.QueueJobCommandAsync(Guid.NewGuid(), (JobCommand)99, CancellationToken.None)
        );

        Assert.Equal("command", exception.ParamName);
    }

    private static IConfiguration Configuration(string databasePath, string secretsRoot) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ControlDatabase:Path"] = databasePath,
                    ["Secrets:Root"] = secretsRoot,
                }
            )
            .Build();

    private static TransferPlanContent SealedContent(
        int sealingVersion = TransferPlanContent.CurrentSealingVersion,
        IReadOnlyList<PlanWarning>? warnings = null,
        IReadOnlyList<PlanTable>? tables = null
    ) =>
        new(
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
            tables ?? [],
            new BatchTarget(1, 1),
            VerificationStrategy.Standard,
            new ManifestCounts(0, 0, 0, 0),
            sealingVersion,
            warnings
        );

    [Fact]
    public async Task DataPitcherApplication_WhenThePlanWasSealedByAnOlderAlgorithm_RefusesToStartAndFlagsTheReview()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        await fixture.Plans.SealAsync(planId, SealedContent(sealingVersion: 0), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<StalePlanException>(() =>
            fixture.Application.StartJobAsync(planId, "stale-start", CancellationToken.None)
        );
        var review = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);

        Assert.Contains("Seal the plan again", exception.Message);
        Assert.Equal("invalidated", review.Seal.Status);
        Assert.Equal("plan_stale", Assert.Single(review.Seal.InvalidationReasons).Code);
        Assert.Equal("plan_stale", Assert.Single(review.Blockers).Code);
    }

    [Fact]
    public async Task DataPitcherApplication_ProjectsSealedWarningsAndDeferredColumnsIntoThePlanReview()
    {
        using var fixture = new ProductionApplicationFixture();
        var planId = Guid.NewGuid();
        _ = await fixture.Application.SavePlanAsync(
            planId,
            new SavePlanRequest("Plan", null, "ignored-on-create"),
            CancellationToken.None
        );
        var teams = new TableAddress("app", "Teams");
        var deferred = new PlanTable(
            new TableMapping(teams, teams, [new ColumnMapping("Id", "Id"), new ColumnMapping("LeadId", "LeadId")]),
            PlanTableState.RequiredDependency,
            new ManifestCounts(1, 1, 1, 0),
            new TopologicalGroup([teams]),
            CycleStrategy.NullableForeignKeyTwoPhase,
            ["LeadId"]
        );
        var nodes = new TableAddress("app", "Nodes");
        var levelled = new PlanTable(
            new TableMapping(nodes, nodes, [new ColumnMapping("Id", "Id"), new ColumnMapping("ParentId", "ParentId")]),
            PlanTableState.Root,
            new ManifestCounts(1, 1, 1, 0),
            new TopologicalGroup([nodes]),
            CycleStrategy.NotApplicable,
            null,
            ["ParentId"]
        );
        await fixture.Plans.SealAsync(
            planId,
            SealedContent(
                warnings: [new PlanWarning("target_constraint_untrusted", "FK_Teams_Lead is untrusted.")],
                tables: [deferred, levelled]
            ),
            CancellationToken.None
        );

        var review = await fixture.Application.GetPlanReviewAsync(planId, CancellationToken.None);

        Assert.Equal("sealed", review.Seal.Status);
        Assert.Empty(review.Blockers);
        var warning = Assert.Single(review.Warnings);
        Assert.Equal(("target_constraint_untrusted", "FK_Teams_Lead is untrusted."), (warning.Code, warning.Message));
        Assert.Equal(2, review.Cycles.Count);
        Assert.Equal("NullableForeignKeyTwoPhase", review.Cycles[0].Strategy);
        Assert.Equal(["app.Teams"], review.Cycles[0].Tables);
        Assert.Contains("LeadId", review.Cycles[0].Message);
        Assert.Equal("Ordered", review.Cycles[1].Strategy);
        Assert.Contains("ParentId", review.Cycles[1].Message);
    }

    private sealed class ProductionApplicationFixture : IDisposable
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"datapitcher-application-{Guid.NewGuid():N}.db"
        );

        private readonly string _secretsRoot = Path.Combine(
            Path.GetTempPath(),
            "datapitcher-secrets-" + Guid.NewGuid().ToString("N")
        );

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
                new ConnectionHealthService(
                    Profiles,
                    new SecretReferenceResolver(_secretsRoot),
                    new ConnectionProviderRegistry([new PostgreSqlConnectionProvider()])
                ),
                snapshots,
                Selections,
                Plans,
                Jobs,
                Events,
                providers: new ConnectionProviderRegistry([new PostgreSqlConnectionProvider()]),
                secretResolver: new SecretReferenceResolver(_secretsRoot),
                secretWriter: new FileSecretStore(_secretsRoot)
            );
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
                new ControlParameter[]
                {
                    new("snapshotId", snapshotId.ToString()),
                    new("connectionId", connectionId.ToString()),
                    new("snapshotHash", "snapshot-hash"),
                    new(
                        "contentJson",
                        content
                            ?? "{\"Tables\":[],\"ForeignKeys\":[],\"DatabaseIdentity\":\"database\",\"ProviderVersion\":\"version\"}"
                    ),
                    new("createdUtc", Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                }
            );
            return snapshotId;
        }

        public void SetPlanHash(Guid planId, string hash)
        {
            using var database = Database.Open();
            database.Execute(
                "UPDATE Plans SET CanonicalHash = @hash WHERE PlanId = @planId",
                new ControlParameter[] { new("hash", hash), new("planId", planId.ToString()) }
            );
        }

        public void Dispose()
        {
            if (File.Exists(_databasePath))
                File.Delete(_databasePath);
            if (Directory.Exists(_secretsRoot))
                Directory.Delete(_secretsRoot, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
