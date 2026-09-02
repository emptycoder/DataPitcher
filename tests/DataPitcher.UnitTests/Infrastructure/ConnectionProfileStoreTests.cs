using System.Diagnostics;
using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Persistence;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ConnectionProfileStoreTests
{
    [Fact]
    public void ConnectionProfileDraft_PreservesProfileMetadata()
    {
        var reference = new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_CONNECTION");
        var draft = new ConnectionProfileDraft("Source", "postgresql", reference, "app", "__datapitcher");

        Assert.Equal("Source", draft.DisplayName);
        Assert.Equal("postgresql", draft.ProviderId);
        Assert.Same(reference, draft.SecretReference);
        Assert.Equal("app", draft.BusinessSchema);
        Assert.Equal("__datapitcher", draft.StagingSchema);
    }

    [Fact]
    public void SchemaRows_PreserveOnlyScanAndSnapshotMetadata()
    {
        var connectionId = Guid.NewGuid().ToString();
        var scan = new SchemaScanRow { ScanId = Guid.NewGuid().ToString(), ConnectionId = connectionId, IdempotencyKey = "scan-01", State = "Queued", SnapshotId = Guid.NewGuid().ToString(), SnapshotHash = "hash", FailureCode = "schema_scan_failed", CreatedUtc = "created", UpdatedUtc = "updated" };
        var snapshot = new SchemaSnapshotRow { SnapshotId = Guid.NewGuid().ToString(), ConnectionId = connectionId, SnapshotHash = "hash", ContentJson = "{}", CreatedUtc = "created" };

        Assert.Equal("scan-01", scan.IdempotencyKey);
        Assert.Equal("Queued", scan.State);
        Assert.NotNull(scan.ScanId);
        Assert.Equal(connectionId, scan.ConnectionId);
        Assert.NotNull(scan.SnapshotId);
        Assert.Equal("hash", scan.SnapshotHash);
        Assert.Equal("schema_scan_failed", scan.FailureCode);
        Assert.Equal("created", scan.CreatedUtc);
        Assert.Equal("updated", scan.UpdatedUtc);
        Assert.NotNull(snapshot.SnapshotId);
        Assert.Equal(connectionId, snapshot.ConnectionId);
        Assert.Equal("hash", snapshot.SnapshotHash);
        Assert.Equal("{}", snapshot.ContentJson);
        Assert.Equal("created", snapshot.CreatedUtc);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenCreatedAndReplayed_ReturnsTheOriginalProfile()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var draft = Draft();

        var created = await store.CreateAsync(draft, "connection-create-01", CancellationToken.None);
        var replayed = await store.CreateAsync(Draft("other"), "connection-create-01", CancellationToken.None);

        Assert.Equal(created.ConnectionId, replayed.ConnectionId);
        Assert.Equal(created.DisplayName, replayed.DisplayName);
        Assert.Equal(created.ProviderId, replayed.ProviderId);
        Assert.Equal(created.SecretReference, replayed.SecretReference);
        Assert.Equal(created.BusinessSchema, replayed.BusinessSchema);
        Assert.Equal(created.StagingSchema, replayed.StagingSchema);
        Assert.Equal(1, created.Version);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenRead_ReturnsNoSecretLocatorOrContent()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        Environment.SetEnvironmentVariable("DP_TEST_SECRET", "password-redaction-sentinel");
        try
        {
            var profile = await store.CreateAsync(Draft(), "connection-create-02", CancellationToken.None);

            var summary = await store.GetSummaryAsync(profile.ConnectionId, CancellationToken.None);
            var summaries = await store.ListSummariesAsync(CancellationToken.None);
            var serialized = JsonSerializer.Serialize(new[] { summary }.Concat(summaries));
            using var db = fixture.Database.Open();
            var stored = db.Query<string>("SELECT ConnectionId || '|' || DisplayName || '|' || ProviderId || '|' || SecretReferenceKind || '|' || SecretReferenceLocator || '|' || BusinessSchema || '|' || StagingSchema || '|' || HealthState || '|' || IdempotencyKey FROM ConnectionProfiles").Single();

            Assert.Contains("DP_TEST_SECRET", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("password-redaction-sentinel", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("DP_TEST_SECRET", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("password-redaction-sentinel", serialized, StringComparison.Ordinal);
            Assert.Equal(SecretReferenceKind.EnvironmentVariable, summary.SecretReferenceKind);
            Assert.Equal(ConnectionHealthState.Unknown, summary.Health);
            Assert.Equal("\"1\"", summary.ETag);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DP_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenReadProfile_ReturnsThePersistedReferenceOnlyToTheServer()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-03", CancellationToken.None);

        var profile = await store.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.Equal(created, profile);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenUpdatedWithCurrentEtag_ReplacesTheReferenceAndVersion()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-04", CancellationToken.None);

        var updated = await store.UpdateAsync(created.ConnectionId, new ConnectionProfileDraft("Target", "sqlserver", new(SecretReferenceKind.EnvironmentVariable, "DP_UPDATED_SECRET"), "dbo", "__datapitcher"), "\"1\"", CancellationToken.None);

        Assert.Equal(created.ConnectionId, updated.ConnectionId);
        Assert.Equal("Target", updated.DisplayName);
        Assert.Equal("sqlserver", updated.ProviderId);
        Assert.Equal("DP_UPDATED_SECRET", updated.SecretReference.Locator);
        Assert.Equal("dbo", updated.BusinessSchema);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenUpdatedWithOldEtag_FailsWithoutChangingTheProfile()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-05", CancellationToken.None);
        _ = await store.UpdateAsync(created.ConnectionId, Draft("updated"), "\"1\"", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(created.ConnectionId, Draft("stale"), "\"1\"", CancellationToken.None));
        var persisted = await store.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.DoesNotContain("DP_TEST_SECRET", exception.Message, StringComparison.Ordinal);
        Assert.Equal("updated", persisted.DisplayName);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenDeletedWithOldEtag_FailsWithoutDeletingTheProfile()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-06", CancellationToken.None);
        _ = await store.UpdateAsync(created.ConnectionId, Draft("updated"), "\"1\"", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(created.ConnectionId, "\"1\"", CancellationToken.None));
        var profile = await store.GetProfileAsync(created.ConnectionId, CancellationToken.None);

        Assert.DoesNotContain("DP_TEST_SECRET", exception.Message, StringComparison.Ordinal);
        Assert.Equal(created.ConnectionId, profile.ConnectionId);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenDeletedWithCurrentEtag_RemovesTheProfileAndOwnedRows()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-07", CancellationToken.None);
        using (var db = fixture.Database.Open())
        {
            db.Execute("INSERT INTO SchemaScans (ScanId, ConnectionId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES (@scanId, @connectionId, @idempotencyKey, @state, @createdUtc, @updatedUtc)", new DataParameter[] { new("scanId", Guid.NewGuid().ToString()), new("connectionId", created.ConnectionId.ToString()), new("idempotencyKey", "scan-01"), new("state", "Queued"), new("createdUtc", fixture.Clock.UtcNow.ToString("O")), new("updatedUtc", fixture.Clock.UtcNow.ToString("O")) });
            db.Execute("INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)", new DataParameter[] { new("snapshotId", Guid.NewGuid().ToString()), new("connectionId", created.ConnectionId.ToString()), new("snapshotHash", "hash"), new("contentJson", "{}"), new("createdUtc", fixture.Clock.UtcNow.ToString("O")) });
        }

        await store.DeleteAsync(created.ConnectionId, "\"1\"", CancellationToken.None);

        using var check = fixture.Database.Open();
        Assert.Equal(0, check.Query<int>("SELECT COUNT(*) FROM ConnectionProfiles").Single());
        Assert.Equal(0, check.Query<int>("SELECT COUNT(*) FROM SchemaScans").Single());
        Assert.Equal(0, check.Query<int>("SELECT COUNT(*) FROM SchemaSnapshots").Single());
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenAssessmentIsSaved_PersistsOnlySafeAssessmentData()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-08", CancellationToken.None);
        var assessment = new ConnectionAssessment(ConnectionHealthState.Degraded, "database", "version", new[] { ConnectionCapability.CanConnect }, Array.Empty<ConnectionCapability>(), new[] { ConnectionCapability.SupportsDurableResume }, "staging_cleanup_failed");

        var summary = await store.SaveAssessmentAsync(created.ConnectionId, TransferMode.ResumableStaged, ConnectionRole.Source, assessment, CancellationToken.None);
        using var db = fixture.Database.Open();

        Assert.Equal(ConnectionHealthState.Degraded, summary.Health);
        Assert.Equal("\"2\"", summary.ETag);
        Assert.Equal("database", db.Query<string>("SELECT DatabaseIdentity FROM ConnectionProfiles").Single());
        Assert.Equal("version", db.Query<string>("SELECT ProviderVersion FROM ConnectionProfiles").Single());
        Assert.Equal("staging_cleanup_failed", db.Query<string>("SELECT CleanupFailureCode FROM ConnectionProfiles").Single());
        Assert.DoesNotContain("DP_TEST_SECRET", db.Query<string>("SELECT CapabilitiesJson FROM ConnectionProfiles").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenAssessmentHasNoCleanupFailure_PersistsNoFailureCode()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var created = await store.CreateAsync(Draft(), "connection-create-08b", CancellationToken.None);

        _ = await store.SaveAssessmentAsync(created.ConnectionId, TransferMode.DirectFast, ConnectionRole.Source, new(ConnectionHealthState.Healthy, "database", "version", Array.Empty<ConnectionCapability>(), Array.Empty<ConnectionCapability>(), Array.Empty<ConnectionCapability>(), null), CancellationToken.None);
        using var db = fixture.Database.Open();

        Assert.Null(db.Query<string?>("SELECT CleanupFailureCode FROM ConnectionProfiles").Single());
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenPersistingProfileOrAssessment_EmitsNoReferenceOrSecretInLogsOrActivity()
    {
        var forbidden = new[] { "password-redaction-sentinel", "token-redaction-sentinel", "client-secret-redaction-sentinel", "Host=db;Password=connection-string-sentinel", "reference-content-sentinel", "DP_TEST_SECRET" };
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var logger = new CapturingLogger<ConnectionProfileStore>();
        var tags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DataPitcher.ConnectionProfiles",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => tags.AddRange(activity.Tags.Select(tag => $"{tag.Key}={tag.Value}")),
        };
        ActivitySource.AddActivityListener(listener);
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock, logger);

        var profile = await store.CreateAsync(Draft(), "connection-create-09", CancellationToken.None);
        var exception = new InvalidOperationException(string.Join('|', forbidden));
        _ = await store.SaveAssessmentAsync(profile.ConnectionId, TransferMode.DirectFast, ConnectionRole.Source, new(ConnectionHealthState.Healthy, "database", "version", new[] { ConnectionCapability.CanConnect }, Array.Empty<ConnectionCapability>(), Array.Empty<ConnectionCapability>(), exception.Message), CancellationToken.None);
        using var db = fixture.Database.Open();
        var stored = db.Query<string>("SELECT COALESCE(CleanupFailureCode, '') || '|' || COALESCE(CapabilitiesJson, '') FROM ConnectionProfiles").Single();

        foreach (var sentinel in forbidden)
        {
            Assert.DoesNotContain(sentinel, string.Join('|', logger.Messages), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, string.Join('|', tags), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, stored, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenIdempotencyKeyIsBlankOrProfileIsMissing_FailsWithFixedSafeErrors()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);

        var blank = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Draft(), "", CancellationToken.None));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetSummaryAsync(Guid.NewGuid(), CancellationToken.None));
        var created = await store.CreateAsync(Draft(), "connection-create-10", CancellationToken.None);
        var malformed = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(created.ConnectionId, Draft(), "invalid", CancellationToken.None));
        var zero = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(created.ConnectionId, "\"0\"", CancellationToken.None));

        Assert.DoesNotContain("DP_TEST_SECRET", blank.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DP_TEST_SECRET", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DP_TEST_SECRET", malformed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DP_TEST_SECRET", zero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionProfileStore_WhenMarkingAMissingProfileAsChecking_FailsWithTheFixedNotFoundError()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkCheckingAsync(Guid.NewGuid(), TransferMode.DirectFast, ConnectionRole.Source, CancellationToken.None));

        Assert.Equal("Connection profile was not found.", exception.Message);
    }

    private static ConnectionProfileDraft Draft(string displayName = "Source") => new(displayName, "postgresql", new(SecretReferenceKind.EnvironmentVariable, "DP_TEST_SECRET"), "app", "__datapitcher");

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Messages.Add(state?.ToString() ?? "");
            if (exception is not null) Messages.Add(exception.ToString());
        }
    }
}
