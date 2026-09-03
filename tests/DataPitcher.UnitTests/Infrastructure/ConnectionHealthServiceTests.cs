using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Worker;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ConnectionHealthServiceTests
{
    [Fact]
    public async Task TestAsync_WhenProbeSucceeds_PersistsOnlyTheClassifierDerivedHealth()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await store.CreateAsync(Draft("source"), "profile-01", CancellationToken.None);
        var detector = new Detector(Evidence());
        var service = new ConnectionHealthService(
            store,
            new Resolver(),
            new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(detector) })
        );

        var summary = await service.TestAsync(
            profile.ConnectionId,
            TransferMode.DirectFast,
            ConnectionRole.Source,
            CancellationToken.None
        );

        Assert.Equal(ConnectionHealthState.Healthy, summary.Health);
        var request = Assert.Single(detector.Requests);
        Assert.Equal(profile.ConnectionId, request.Profile.ConnectionId);
        Assert.Equal(TransferMode.DirectFast, request.Mode);
        Assert.Equal(ConnectionRole.Source, request.Role);
    }

    [Fact]
    public async Task RecheckAsync_MarksCheckingBeforeResolvingAndPersistsUnhealthyForMissingRequirements()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await store.CreateAsync(Draft("source"), "profile-02", CancellationToken.None);
        var resolver = new Resolver
        {
            BeforeResolve = async () =>
                (await store.GetSummaryAsync(profile.ConnectionId, CancellationToken.None)).Health,
        };
        var service = new ConnectionHealthService(
            store,
            resolver,
            new ConnectionProviderRegistry(
                new IConnectionProvider[]
                {
                    new Provider(
                        new Detector(
                            new ConnectionProbeEvidence(
                                "identity",
                                "version",
                                new[] { ConnectionCapability.CanConnect },
                                null
                            )
                        )
                    ),
                }
            )
        );

        var summary = await service.RecheckAsync(
            profile.ConnectionId,
            TransferMode.DirectFast,
            ConnectionRole.Source,
            CancellationToken.None
        );

        Assert.Equal(ConnectionHealthState.Checking, resolver.HealthAtResolve);
        Assert.Equal(ConnectionHealthState.Unhealthy, summary.Health);
    }

    [Fact]
    public async Task TestAsync_WhenProbeFails_PersistsAnUnhealthyAssessment()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var profile = await store.CreateAsync(Draft("source"), "profile-probe-failure", CancellationToken.None);
        var service = new ConnectionHealthService(
            store,
            new Resolver(),
            new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(new ThrowingDetector()) })
        );

        var summary = await service.TestAsync(
            profile.ConnectionId,
            TransferMode.DirectFast,
            ConnectionRole.Source,
            CancellationToken.None
        );

        Assert.Equal(ConnectionHealthState.Unhealthy, summary.Health);
    }

    [Fact]
    public async Task RevalidateAsync_WhenTargetIsNotHealthy_ProbesBothLoadedConnectionRolesAndThrowsSafely()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var source = await store.CreateAsync(Draft("source"), "profile-03", CancellationToken.None);
        var target = await store.CreateAsync(Draft("target"), "profile-04", CancellationToken.None);
        var detector = new Detector(
            Evidence(),
            new ConnectionProbeEvidence("identity", "version", new[] { ConnectionCapability.CanConnect }, null)
        );
        var service = new ConnectionHealthService(
            store,
            new Resolver(),
            new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(detector) })
        );
        var run = new TransferRun(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "seal",
            true,
            source.ConnectionId,
            target.ConnectionId,
            TransferMode.DirectFast
        );

        var exception = await Assert.ThrowsAsync<ConnectionNotHealthyException>(() =>
            service.RevalidateAsync(run, CancellationToken.None)
        );

        Assert.Equal("Connection health revalidation failed.", exception.Message);
        Assert.Equal(
            new[] { ConnectionRole.Source, ConnectionRole.Target },
            detector.Requests.Select(request => request.Role)
        );
        Assert.Equal(
            new[] { source.ConnectionId, target.ConnectionId },
            detector.Requests.Select(request => request.Profile.ConnectionId)
        );
        Assert.All(detector.Requests, request => Assert.Equal(TransferMode.DirectFast, request.Mode));
    }

    [Fact]
    public async Task RevalidateAsync_WhenBothConnectionsAreHealthy_ProbesBothAndSucceeds()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var source = await store.CreateAsync(Draft("source"), "profile-revalidate-source", CancellationToken.None);
        var target = await store.CreateAsync(Draft("target"), "profile-revalidate-target", CancellationToken.None);
        var detector = new Detector(Evidence(), TargetEvidence());
        var service = new ConnectionHealthService(
            store,
            new Resolver(),
            new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(detector) })
        );
        var run = new TransferRun(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "seal",
            true,
            source.ConnectionId,
            target.ConnectionId,
            TransferMode.DirectFast
        );

        await service.RevalidateAsync(run, CancellationToken.None);

        Assert.Equal(
            new[] { ConnectionRole.Source, ConnectionRole.Target },
            detector.Requests.Select(request => request.Role)
        );
    }

    [Fact]
    public async Task RevalidateAsync_WhenSourceIsNotHealthy_DoesNotProbeTheTarget()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var source = await store.CreateAsync(Draft("source"), "profile-05", CancellationToken.None);
        var target = await store.CreateAsync(Draft("target"), "profile-06", CancellationToken.None);
        var detector = new Detector(
            new ConnectionProbeEvidence("identity", "version", Array.Empty<ConnectionCapability>(), null)
        );
        var service = new ConnectionHealthService(
            store,
            new Resolver(),
            new ConnectionProviderRegistry(new IConnectionProvider[] { new Provider(detector) })
        );
        var run = new TransferRun(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "seal",
            true,
            source.ConnectionId,
            target.ConnectionId,
            TransferMode.DirectFast
        );

        await Assert.ThrowsAsync<ConnectionNotHealthyException>(() =>
            service.RevalidateAsync(run, CancellationToken.None)
        );

        Assert.Single(detector.Requests, request => request.Role is ConnectionRole.Source);
    }

    private static ConnectionProfileDraft Draft(string name) =>
        new(
            name,
            "postgresql",
            new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_HEALTH_SECRET"),
            "app",
            "__datapitcher"
        );

    private static ConnectionProbeEvidence Evidence() =>
        new(
            "identity",
            "version",
            new[]
            {
                ConnectionCapability.CanConnect,
                ConnectionCapability.CanReadSchema,
                ConnectionCapability.CanReadBusinessRows,
                ConnectionCapability.CanUseTransactions,
                ConnectionCapability.CanUseSnapshotIsolation,
            },
            null
        );

    private static ConnectionProbeEvidence TargetEvidence() =>
        new(
            "identity",
            "version",
            new[]
            {
                ConnectionCapability.CanConnect,
                ConnectionCapability.CanReadSchema,
                ConnectionCapability.CanReadBusinessRows,
                ConnectionCapability.CanBulkInsert,
                ConnectionCapability.CanPreserveIdentity,
                ConnectionCapability.CanUseTransactions,
                ConnectionCapability.CanUseSnapshotIsolation,
            },
            null
        );

    private sealed class Resolver : ISecretReferenceResolver
    {
        public Func<Task<ConnectionHealthState>>? BeforeResolve { get; init; }
        public ConnectionHealthState? HealthAtResolve { get; private set; }

        public async Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            if (BeforeResolve is not null)
                HealthAtResolve = await BeforeResolve();
            return "resolved-secret";
        }
    }

    private sealed class Detector(params ConnectionProbeEvidence[] evidence) : ICapabilityDetector
    {
        private readonly Queue<ConnectionProbeEvidence> _evidence = new(evidence);
        public List<ConnectionProbeRequest> Requests { get; } = [];

        public Task<ConnectionProbeEvidence> ProbeAsync(
            ConnectionProbeRequest request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(_evidence.Dequeue());
        }
    }

    private sealed class ThrowingDetector : ICapabilityDetector
    {
        public Task<ConnectionProbeEvidence> ProbeAsync(
            ConnectionProbeRequest request,
            CancellationToken cancellationToken
        ) => Task.FromException<ConnectionProbeEvidence>(new InvalidOperationException("probe failed"));
    }

    private sealed class Provider(ICapabilityDetector detector) : IConnectionProvider
    {
        public string ProviderId => "postgresql";
        public ICapabilityDetector CapabilityDetector { get; } = detector;
        public ISchemaIntrospector SchemaIntrospector => throw new NotSupportedException();
    }
}
