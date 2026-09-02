using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataPitcher.Infrastructure.Connections;

public sealed record ConnectionProfileDraft(string DisplayName, string ProviderId, SecretReference SecretReference, string BusinessSchema, string StagingSchema);

public sealed class ConnectionProfileStore(ControlDatabase database, IClock clock, ILogger<ConnectionProfileStore>? logger = null)
{
    private static readonly ActivitySource ActivitySource = new("DataPitcher.ConnectionProfiles");
    private readonly ILogger<ConnectionProfileStore> _logger = logger ?? NullLogger<ConnectionProfileStore>.Instance;

    public Task<ConnectionProfile> CreateAsync(ConnectionProfileDraft draft, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var row = new ConnectionProfileRow
        {
            ConnectionId = Guid.NewGuid().ToString(),
            DisplayName = draft.DisplayName,
            ProviderId = draft.ProviderId,
            SecretReferenceKind = draft.SecretReference.Kind.ToString(),
            SecretReferenceLocator = draft.SecretReference.Locator,
            BusinessSchema = draft.BusinessSchema,
            StagingSchema = draft.StagingSchema,
            Version = 1,
            HealthState = ConnectionHealthState.Unknown.ToString(),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        var inserted = db.Execute("INSERT OR IGNORE INTO ConnectionProfiles (ConnectionId, DisplayName, ProviderId, SecretReferenceKind, SecretReferenceLocator, BusinessSchema, StagingSchema, Version, HealthState, CreatedUtc, UpdatedUtc, IdempotencyKey) VALUES (@connectionId, @displayName, @providerId, @secretReferenceKind, @secretReferenceLocator, @businessSchema, @stagingSchema, @version, @healthState, @createdUtc, @updatedUtc, @idempotencyKey)", Parameters(row, idempotencyKey));
        if (inserted == 0)
        {
            var existing = db.GetTable<ConnectionProfileRow>().Single(profile => profile.IdempotencyKey == idempotencyKey);
            return Task.FromResult(ToProfile(existing));
        }
        transaction.Commit();
        return Task.FromResult(ToProfile(row));
    }

    public Task<ConnectionProfileSummary> GetSummaryAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        return Task.FromResult(ToSummary(GetRow(db, connectionId)));
    }

    public Task<IReadOnlyList<ConnectionProfileSummary>> ListSummariesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        IReadOnlyList<ConnectionProfileSummary> summaries = db.GetTable<ConnectionProfileRow>().ToArray().OrderBy(profile => profile.DisplayName, StringComparer.Ordinal).ThenBy(profile => profile.ConnectionId, StringComparer.Ordinal).Select(ToSummary).ToArray();
        return Task.FromResult(summaries);
    }

    public Task<ConnectionProfile> UpdateAsync(Guid connectionId, ConnectionProfileDraft draft, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var existing = GetRow(db, connectionId);
        var version = ParseEtag(ifMatch);
        var now = Stamp(clock.UtcNow);
        var affected = db.Execute("UPDATE ConnectionProfiles SET DisplayName = @displayName, ProviderId = @providerId, SecretReferenceKind = @secretReferenceKind, SecretReferenceLocator = @secretReferenceLocator, BusinessSchema = @businessSchema, StagingSchema = @stagingSchema, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE ConnectionId = @connectionId AND Version = @version", new DataParameter[] { new("displayName", draft.DisplayName), new("providerId", draft.ProviderId), new("secretReferenceKind", draft.SecretReference.Kind.ToString()), new("secretReferenceLocator", draft.SecretReference.Locator), new("businessSchema", draft.BusinessSchema), new("stagingSchema", draft.StagingSchema), new("updatedUtc", now), new("connectionId", connectionId.ToString()), new("version", version) });
        if (affected != 1) throw new InvalidOperationException("Connection profile version does not match.");
        transaction.Commit();
        return Task.FromResult(new ConnectionProfile(connectionId, draft.DisplayName, draft.ProviderId, draft.SecretReference, draft.BusinessSchema, draft.StagingSchema, existing.Version + 1));
    }

    public Task DeleteAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var affected = db.Execute("DELETE FROM ConnectionProfiles WHERE ConnectionId = @connectionId AND Version = @version", new DataParameter[] { new("connectionId", connectionId.ToString()), new("version", ParseEtag(ifMatch)) });
        if (affected != 1) throw new InvalidOperationException("Connection profile version does not match.");
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<ConnectionProfile> GetProfileAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        return Task.FromResult(ToProfile(GetRow(db, connectionId)));
    }

    public Task<ConnectionProfileSummary> SaveAssessmentAsync(Guid connectionId, TransferMode mode, ConnectionRole role, ConnectionAssessment assessment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var profile = GetRow(db, connectionId);
        var available = JsonSerializer.Serialize(assessment.Available.Select(capability => capability.ToString()).OrderBy(capability => capability, StringComparer.Ordinal));
        var failureCode = SafeFailureCode(assessment.CleanupFailureCode);
        db.Execute("UPDATE ConnectionProfiles SET HealthState = @healthState, AssessmentMode = @assessmentMode, AssessmentRole = @assessmentRole, DatabaseIdentity = @databaseIdentity, ProviderVersion = @providerVersion, CapabilitiesJson = @capabilitiesJson, CleanupFailureCode = @cleanupFailureCode, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE ConnectionId = @connectionId", new DataParameter[] { new("healthState", assessment.State.ToString()), new("assessmentMode", mode.ToString()), new("assessmentRole", role.ToString()), new("databaseIdentity", assessment.DatabaseIdentity), new("providerVersion", assessment.ProviderVersion), new("capabilitiesJson", available), new("cleanupFailureCode", failureCode), new("updatedUtc", Stamp(clock.UtcNow)), new("connectionId", connectionId.ToString()) });
        transaction.Commit();
        EmitAssessment(profile, assessment.State, assessment.Available, failureCode);
        profile.Version++;
        profile.HealthState = assessment.State.ToString();
        return Task.FromResult(ToSummary(profile));
    }

    private void EmitAssessment(ConnectionProfileRow profile, ConnectionHealthState state, IEnumerable<ConnectionCapability> available, string? failureCode)
    {
        var capabilities = string.Join(',', available.Select(capability => capability.ToString()).OrderBy(capability => capability, StringComparer.Ordinal));
        _logger.LogInformation("Connection profile assessed {ConnectionId} {ProviderId} {State} {Capabilities} {ErrorCode}", profile.ConnectionId, profile.ProviderId, state, capabilities, failureCode);
        using var activity = ActivitySource.StartActivity("connection.profile.assessed");
        activity?.SetTag("connection.id", profile.ConnectionId);
        activity?.SetTag("provider.id", profile.ProviderId);
        activity?.SetTag("health.state", state.ToString());
        activity?.SetTag("capabilities", capabilities);
        activity?.SetTag("error.code", failureCode);
    }

    private static ConnectionProfileRow GetRow(DataConnection db, Guid connectionId) => db.GetTable<ConnectionProfileRow>().SingleOrDefault(profile => profile.ConnectionId == connectionId.ToString()) ?? throw new InvalidOperationException("Connection profile was not found.");
    private static ConnectionProfile ToProfile(ConnectionProfileRow row) => new(Guid.Parse(row.ConnectionId), row.DisplayName, row.ProviderId, new(Enum.Parse<SecretReferenceKind>(row.SecretReferenceKind), row.SecretReferenceLocator), row.BusinessSchema, row.StagingSchema, row.Version);
    private static ConnectionProfileSummary ToSummary(ConnectionProfileRow row) => new(Guid.Parse(row.ConnectionId), row.DisplayName, row.ProviderId, Enum.Parse<SecretReferenceKind>(row.SecretReferenceKind), Enum.Parse<ConnectionHealthState>(row.HealthState), Etag(row.Version));
    private static DataParameter[] Parameters(ConnectionProfileRow row, string idempotencyKey) => [new("connectionId", row.ConnectionId), new("displayName", row.DisplayName), new("providerId", row.ProviderId), new("secretReferenceKind", row.SecretReferenceKind), new("secretReferenceLocator", row.SecretReferenceLocator), new("businessSchema", row.BusinessSchema), new("stagingSchema", row.StagingSchema), new("version", row.Version), new("healthState", row.HealthState), new("createdUtc", row.CreatedUtc), new("updatedUtc", row.UpdatedUtc), new("idempotencyKey", idempotencyKey)];
    private static string Etag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    private static long ParseEtag(string value) => long.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0 ? version : throw new InvalidOperationException("Connection profile version does not match.");
    private static string? SafeFailureCode(string? value) => value is null ? null : value.Equals("staging_cleanup_failed", StringComparison.Ordinal) ? value : "connection_failed";
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
