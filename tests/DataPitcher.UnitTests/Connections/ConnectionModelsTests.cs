using System.Globalization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Connections;

public sealed class ConnectionModelsTests
{
    [Theory]
    [InlineData(SecretReferenceKind.EnvironmentVariable, "DP_CONNECTION")]
    [InlineData(SecretReferenceKind.FileMounted, "/secrets/connection")]
    public void SecretReference_WhenLocatorIsValid_PreservesMetadata(SecretReferenceKind kind, string locator)
    {
        var reference = new SecretReference(kind, locator);

        Assert.Equal(kind, reference.Kind);
        Assert.Equal(locator, reference.Locator);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SecretReference_WhenLocatorIsBlank_Throws(string locator) =>
        Assert.Throws<ArgumentException>(() => new SecretReference(SecretReferenceKind.EnvironmentVariable, locator));

    [Fact]
    public void SecretReference_WhenMountedLocatorIsRelative_Throws() =>
        Assert.Throws<ArgumentException>(() => new SecretReference(SecretReferenceKind.FileMounted, "secrets/connection"));

    [Fact]
    public void ConnectionProfileAndSummary_PreserveSafeMetadata()
    {
        var connectionId = Guid.NewGuid();
        var profile = new ConnectionProfile(connectionId, "source", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "DP_CONNECTION"), "app", "__datapitcher", 1);
        var copy = profile with { Version = 2 };
        var summary = new ConnectionProfileSummary(connectionId, "source", "postgresql", SecretReferenceKind.EnvironmentVariable, ConnectionHealthState.Unknown, "\"1\"");

        Assert.Equal(connectionId, profile.ConnectionId);
        Assert.Equal("source", profile.DisplayName);
        Assert.Equal("postgresql", profile.ProviderId);
        Assert.Equal(SecretReferenceKind.EnvironmentVariable, profile.SecretReference.Kind);
        Assert.Equal("app", profile.BusinessSchema);
        Assert.Equal("__datapitcher", profile.StagingSchema);
        Assert.Equal(2, copy.Version);
        Assert.Equal(connectionId, summary.ConnectionId);
        Assert.Equal("source", summary.DisplayName);
        Assert.Equal("postgresql", summary.ProviderId);
        Assert.Equal(SecretReferenceKind.EnvironmentVariable, summary.SecretReferenceKind);
        Assert.Equal(ConnectionHealthState.Unknown, summary.Health);
        Assert.Equal("\"1\"", summary.ETag);
    }

    [Fact]
    public void ConnectionRequirements_WhenInputCollectionsChange_RetainsFrozenCopies()
    {
        var required = new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect };
        var optional = new HashSet<ConnectionCapability> { ConnectionCapability.CanUseSnapshotIsolation };
        var requirements = new ConnectionRequirements(required, optional);
        required.Add(ConnectionCapability.CanReadSchema);
        optional.Clear();

        Assert.DoesNotContain(ConnectionCapability.CanReadSchema, requirements.Required);
        Assert.Contains(ConnectionCapability.CanUseSnapshotIsolation, requirements.Optional);
        Assert.Throws<NotSupportedException>(() => ((ISet<ConnectionCapability>)requirements.Required).Add(ConnectionCapability.CanReadSchema));
    }

    [Theory]
    [InlineData(TransferMode.DirectFast, ConnectionRole.Source, ConnectionCapability.CanConnect, true)]
    [InlineData(TransferMode.DirectFast, ConnectionRole.Target, ConnectionCapability.CanBulkInsert, true)]
    [InlineData(TransferMode.ResumableStaged, ConnectionRole.Source, ConnectionCapability.SupportsDurableResume, false)]
    [InlineData(TransferMode.ResumableStaged, ConnectionRole.Target, ConnectionCapability.CanCreateTargetStaging, true)]
    [InlineData(TransferMode.ServerSide, ConnectionRole.Source, ConnectionCapability.CanUseServerSideTransfer, true)]
    [InlineData(TransferMode.ServerSide, ConnectionRole.Target, ConnectionCapability.CanUseServerSideTransfer, true)]
    public void ConnectionRequirements_ForModeAndRole_DeclaresExpectedCapability(TransferMode mode, ConnectionRole role, ConnectionCapability capability, bool isRequired)
    {
        var requirements = ConnectionRequirements.For(mode, role);

        Assert.Equal(isRequired, requirements.Required.Contains(capability));
        Assert.Equal(!isRequired, requirements.Optional.Contains(capability));
    }

    [Fact]
    public void ConnectionProbeEvidence_WhenInputAvailabilityChanges_RetainsFrozenCopy()
    {
        var available = new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect };
        var evidence = new ConnectionProbeEvidence("identity", "version", available, null);
        available.Add(ConnectionCapability.CanReadSchema);

        Assert.Equal("identity", evidence.DatabaseIdentity);
        Assert.Equal("version", evidence.ProviderVersion);
        Assert.Null(evidence.CleanupFailureCode);
        Assert.DoesNotContain(ConnectionCapability.CanReadSchema, evidence.Available);
    }

    [Fact]
    public void ConnectionHealthClassifier_WhenEveryCapabilityIsAvailable_IsHealthy()
    {
        var assessment = ConnectionHealthClassifier.Classify(Requirements(), Evidence());

        Assert.Equal(ConnectionHealthState.Healthy, assessment.State);
        Assert.Empty(assessment.MissingRequired);
        Assert.Empty(assessment.MissingOptional);
        Assert.Equal("identity", assessment.DatabaseIdentity);
        Assert.Equal("version", assessment.ProviderVersion);
        Assert.Null(assessment.CleanupFailureCode);
        Assert.Contains(ConnectionCapability.CanConnect, assessment.Available);
    }

    [Fact]
    public void ConnectionHealthClassifier_WhenOnlyOptionalCapabilityIsMissing_IsDegraded()
    {
        var requirements = new ConnectionRequirements(
            new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema },
            new HashSet<ConnectionCapability> { ConnectionCapability.SupportsDurableResume });
        var assessment = ConnectionHealthClassifier.Classify(
            requirements,
            new ConnectionProbeEvidence("identity", "version",
                new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema }, null));

        Assert.Equal(ConnectionHealthState.Degraded, assessment.State);
        Assert.Single(assessment.MissingOptional, capability => capability == ConnectionCapability.SupportsDurableResume);
    }

    [Fact]
    public void ConnectionHealthClassifier_WhenRequiredCapabilityIsMissing_IsUnhealthy()
    {
        var assessment = ConnectionHealthClassifier.Classify(Requirements(), Evidence(ConnectionCapability.CanReadSchema));

        Assert.Equal(ConnectionHealthState.Unhealthy, assessment.State);
        Assert.Single(assessment.MissingRequired, capability => capability == ConnectionCapability.CanReadSchema);
    }

    [Fact]
    public void ConnectionHealthClassifier_WhenStagingCleanupFails_IsUnhealthy()
    {
        var assessment = ConnectionHealthClassifier.Classify(Requirements(), Evidence(cleanupFailureCode: "staging_cleanup_failed"));

        Assert.Equal(ConnectionHealthState.Unhealthy, assessment.State);
        Assert.Equal("staging_cleanup_failed", assessment.CleanupFailureCode);
    }

    [Theory]
    [InlineData(ConnectionHealthState.Unknown)]
    [InlineData(ConnectionHealthState.Checking)]
    public void ConnectionAssessment_CanRepresentPersistedNonterminalStates(ConnectionHealthState state)
    {
        var assessment = new ConnectionAssessment(state, "identity", "version", [], [], [], null);

        Assert.Equal(state, assessment.State);
    }

    [Fact]
    public void ConnectionAssessment_WhenInputCollectionsChange_RetainsFrozenCopies()
    {
        var available = new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect };
        var missingRequired = new HashSet<ConnectionCapability> { ConnectionCapability.CanReadSchema };
        var missingOptional = new HashSet<ConnectionCapability> { ConnectionCapability.CanUseSnapshotIsolation };
        var assessment = new ConnectionAssessment(ConnectionHealthState.Degraded, "identity", "version", available, missingRequired, missingOptional, null);
        available.Clear();
        missingRequired.Clear();
        missingOptional.Clear();

        Assert.Contains(ConnectionCapability.CanConnect, assessment.Available);
        Assert.Contains(ConnectionCapability.CanReadSchema, assessment.MissingRequired);
        Assert.Contains(ConnectionCapability.CanUseSnapshotIsolation, assessment.MissingOptional);
    }

    [Fact]
    public void SchemaSnapshotContent_WhenInputCollectionsChange_RetainsReadOnlyCopies()
    {
        var tables = new List<SchemaTable> { Orders() };
        var foreignKeys = new List<SchemaForeignKey> { ForeignKey() };
        var content = new SchemaSnapshotContent(tables, foreignKeys);
        tables.Clear();
        foreignKeys.Clear();

        Assert.Single(content.Tables);
        Assert.Single(content.ForeignKeys);
        Assert.Throws<NotSupportedException>(() => ((IList<SchemaTable>)content.Tables).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SchemaForeignKey>)content.ForeignKeys).Clear());
    }

    [Fact]
    public void SchemaSnapshotProjections_WhenInputCollectionsChange_RetainReadOnlyCopies()
    {
        var tables = new List<SchemaTableAddress> { new("app", "Orders") };
        var edges = new List<SchemaGraphEdge> { new(new("app", "Orders"), new("app", "Customers"), "FK_Orders_Customers") };
        var graph = new SchemaGraphProjection(tables, edges);
        var detail = new SchemaTableProjection(Orders(), [ForeignKey()]);
        var neighbourhood = new SchemaNeighbourhoodProjection(new("app", "Orders"), 1, tables, edges);
        tables.Clear();
        edges.Clear();

        Assert.Single(graph.Tables);
        Assert.Single(graph.Edges);
        Assert.Equal("Orders", detail.Table.Name);
        Assert.Single(detail.ForeignKeys);
        Assert.Equal(new SchemaTableAddress("app", "Orders"), neighbourhood.Center);
        Assert.Equal(1, neighbourhood.Depth);
        Assert.Single(neighbourhood.Tables);
        Assert.Single(neighbourhood.Edges);
    }

    [Fact]
    public void StoredSchemaSnapshot_PreservesImmutableContentAndMetadata()
    {
        var snapshotId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var capturedAtUtc = DateTimeOffset.Parse("2026-09-02T00:00:00+00:00", CultureInfo.InvariantCulture);
        var snapshot = new StoredSchemaSnapshot(snapshotId, connectionId, "ABC", capturedAtUtc, Content());

        Assert.Equal(snapshotId, snapshot.SnapshotId);
        Assert.Equal(connectionId, snapshot.ConnectionId);
        Assert.Equal("ABC", snapshot.Hash);
        Assert.Equal(capturedAtUtc, snapshot.CapturedAtUtc);
        Assert.Equal(2, snapshot.Content.Tables.Count);
    }

    [Fact]
    public void CanonicalSchemaSnapshotHasher_WhenInputOrderAndDisplayFactsDiffer_HashesEqually()
    {
        var first = Content();
        var second = new SchemaSnapshotContent([Customers(), Orders()], [ForeignKey()], "different identity", "different version");

        Assert.Equal("identity", first.DatabaseIdentity);
        Assert.Equal("version", first.ProviderVersion);
        Assert.Equal(CanonicalSchemaSnapshotHasher.Hash(first), CanonicalSchemaSnapshotHasher.Hash(second));
    }

    [Theory]
    [InlineData("column")]
    [InlineData("key-order")]
    [InlineData("fk-enforced")]
    [InlineData("fk-column-order")]
    public void CanonicalSchemaSnapshotHasher_WhenTransferRelevantMetadataChanges_HashChanges(string change)
    {
        Assert.NotEqual(CanonicalSchemaSnapshotHasher.Hash(Content()), CanonicalSchemaSnapshotHasher.Hash(Changed(change)));
    }

    [Fact]
    public async Task ConnectionProviderRegistry_WhenProviderIsRegistered_ReturnsItsContracts()
    {
        var detector = new Detector();
        var introspector = new Introspector();
        var provider = new Provider("postgresql", detector, introspector);
        var registry = new ConnectionProviderRegistry([provider]);
        var profile = new ConnectionProfile(Guid.NewGuid(), "source", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "DP_CONNECTION"), "app", "__datapitcher", 1);
        var request = new ConnectionProbeRequest(profile, ConnectionRole.Source, TransferMode.DirectFast, "resolved-secret");

        Assert.Same(provider, registry.Get("postgresql"));
        Assert.Same(detector, provider.CapabilityDetector);
        Assert.Same(introspector, provider.SchemaIntrospector);
        Assert.Equal("postgresql", provider.ProviderId);
        Assert.Equal(profile, request.Profile);
        Assert.Equal(ConnectionRole.Source, request.Role);
        Assert.Equal(TransferMode.DirectFast, request.Mode);
        Assert.Equal("resolved-secret", request.ResolvedConnectionString);
        Assert.Equal("identity", (await detector.ProbeAsync(request, CancellationToken.None)).DatabaseIdentity);
        Assert.Equal(2, (await introspector.ReadAsync(profile, "resolved-secret", CancellationToken.None)).Tables.Count);
    }

    [Fact]
    public void ConnectionProviderRegistry_WhenProviderIsUnsupported_UsesFixedSafeCode()
    {
        var exception = Assert.Throws<UnsupportedConnectionProviderException>(() => new ConnectionProviderRegistry([]).Get("password-redaction-sentinel"));

        Assert.Equal("unsupported_provider", exception.Code);
        Assert.DoesNotContain("password-redaction-sentinel", exception.Message, StringComparison.Ordinal);
    }

    private static ConnectionRequirements Requirements() => new(
        [ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema],
        [ConnectionCapability.CanUseSnapshotIsolation]);

    private static ConnectionProbeEvidence Evidence(ConnectionCapability? omitted = null, string? cleanupFailureCode = null) => new(
        "identity", "version", new[] { ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema, ConnectionCapability.CanUseSnapshotIsolation }
            .Where(capability => capability != omitted), cleanupFailureCode);

    private static SchemaSnapshotContent Content() => new([Orders(), Customers()], [ForeignKey()], "identity", "version");

    private static SchemaSnapshotContent Changed(string change) => change switch
    {
        "column" => new([Orders("CustomerNumber"), Customers()], [ForeignKey()], "identity", "version"),
        "key-order" => new([Orders(keyColumns: ["Number", "Id"]), Customers()], [ForeignKey()], "identity", "version"),
        "fk-enforced" => new([Orders(), Customers()], [ForeignKey(isEnforced: false)], "identity", "version"),
        "fk-column-order" => new([Orders(), Customers(columns: ["Id", "Number"])], [ForeignKey(childColumns: ["Number", "CustomerId"], parentColumns: ["Number", "Id"])], "identity", "version"),
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    private static SchemaTable Orders(string customerColumn = "CustomerId", IReadOnlyList<string>? keyColumns = null) => new(
        "app", "Orders", [new("Id", "int", "System.Int32", false), new(customerColumn, "int", "System.Int32", false), new("Number", "int", "System.Int32", false)],
        new("PK_Orders", keyColumns ?? ["Id", "Number"]), []);

    private static SchemaTable Customers(IReadOnlyList<string>? columns = null) => new(
        "app", "Customers", (columns ?? ["Id"]).Select(name => new SchemaColumn(name, "int", "System.Int32", false)), new("PK_Customers", ["Id"]), []);

    private static SchemaForeignKey ForeignKey(bool isEnforced = true, IReadOnlyList<string>? childColumns = null, IReadOnlyList<string>? parentColumns = null) => new(
        "FK_Orders_Customers", new("app", "Orders"), new("app", "Customers"), childColumns ?? ["CustomerId"], parentColumns ?? ["Id"], isEnforced, true);

    private sealed class Detector : ICapabilityDetector
    {
        public Task<ConnectionProbeEvidence> ProbeAsync(ConnectionProbeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Evidence());
    }

    private sealed class Introspector : ISchemaIntrospector
    {
        public Task<SchemaSnapshotContent> ReadAsync(ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken) =>
            Task.FromResult(Content());
    }

    private sealed class Provider(string providerId, ICapabilityDetector detector, ISchemaIntrospector introspector) : IConnectionProvider
    {
        public string ProviderId { get; } = providerId;
        public ICapabilityDetector CapabilityDetector { get; } = detector;
        public ISchemaIntrospector SchemaIntrospector { get; } = introspector;
    }
}
