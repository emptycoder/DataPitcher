using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataPitcher.ControlStore;

public sealed class ConnectionProfileStore(
    ControlDatabase database,
    IClock clock,
    ILogger<ConnectionProfileStore>? logger = null
) : IConnectionProfileRepository
{
    private const string SelectColumns =
        "SELECT ConnectionId, DisplayName, ProviderId, SecretReferenceKind, SecretReferenceLocator, BusinessSchema, StagingSchema, Version, HealthState FROM ConnectionProfiles";

    private static readonly ActivitySource ActivitySource = new("DataPitcher.ConnectionProfiles");
    private readonly ILogger<ConnectionProfileStore> _logger = logger ?? NullLogger<ConnectionProfileStore>.Instance;

    public Task<ConnectionProfile> CreateAsync(
        ConnectionProfileDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var db = database.OpenNative();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var row = new Row(
            Guid.NewGuid().ToString(),
            draft.DisplayName,
            draft.ProviderId,
            draft.SecretReference.Kind.ToString(),
            draft.SecretReference.Locator,
            draft.BusinessSchema,
            draft.StagingSchema,
            1,
            ConnectionHealthState.Unknown.ToString()
        );
        var inserted = db.Execute(
            "INSERT OR IGNORE INTO ConnectionProfiles (ConnectionId, DisplayName, ProviderId, SecretReferenceKind, SecretReferenceLocator, BusinessSchema, StagingSchema, Version, HealthState, CreatedUtc, UpdatedUtc, IdempotencyKey) VALUES (@connectionId, @displayName, @providerId, @secretReferenceKind, @secretReferenceLocator, @businessSchema, @stagingSchema, @version, @healthState, @createdUtc, @updatedUtc, @idempotencyKey)",
            new ControlParameter("connectionId", row.ConnectionId),
            new ControlParameter("displayName", row.DisplayName),
            new ControlParameter("providerId", row.ProviderId),
            new ControlParameter("secretReferenceKind", row.SecretReferenceKind),
            new ControlParameter("secretReferenceLocator", row.SecretReferenceLocator),
            new ControlParameter("businessSchema", row.BusinessSchema),
            new ControlParameter("stagingSchema", row.StagingSchema),
            new ControlParameter("version", row.Version),
            new ControlParameter("healthState", row.HealthState),
            new ControlParameter("createdUtc", now),
            new ControlParameter("updatedUtc", now),
            new ControlParameter("idempotencyKey", idempotencyKey)
        );
        if (inserted == 0)
        {
            var existing =
                db.Single(
                    SelectColumns + " WHERE IdempotencyKey = @idempotencyKey",
                    Map,
                    new ControlParameter("idempotencyKey", idempotencyKey)
                ) ?? throw new InvalidOperationException("Sequence contains no elements");
            return Task.FromResult(ToProfile(existing));
        }
        transaction.Commit();
        return Task.FromResult(ToProfile(row));
    }

    public Task<ConnectionProfileSummary> GetSummaryAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        return Task.FromResult(ToSummary(GetRow(db, connectionId)));
    }

    public Task<IReadOnlyList<ConnectionProfileSummary>> ListSummariesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        IReadOnlyList<ConnectionProfileSummary> summaries = db.Query(SelectColumns, Map)
            .OrderBy(profile => profile.DisplayName, StringComparer.Ordinal)
            .ThenBy(profile => profile.ConnectionId, StringComparer.Ordinal)
            .Select(ToSummary)
            .ToArray();
        return Task.FromResult(summaries);
    }

    public Task<ConnectionProfile> UpdateAsync(
        Guid connectionId,
        ConnectionProfileDraft draft,
        string ifMatch,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        using var transaction = db.BeginTransaction();
        var existing = GetRow(db, connectionId);
        var version = ParseEtag(ifMatch);
        var now = Stamp(clock.UtcNow);
        var affected = db.Execute(
            "UPDATE ConnectionProfiles SET DisplayName = @displayName, ProviderId = @providerId, SecretReferenceKind = @secretReferenceKind, SecretReferenceLocator = @secretReferenceLocator, BusinessSchema = @businessSchema, StagingSchema = @stagingSchema, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE ConnectionId = @connectionId AND Version = @version",
            new ControlParameter("displayName", draft.DisplayName),
            new ControlParameter("providerId", draft.ProviderId),
            new ControlParameter("secretReferenceKind", draft.SecretReference.Kind.ToString()),
            new ControlParameter("secretReferenceLocator", draft.SecretReference.Locator),
            new ControlParameter("businessSchema", draft.BusinessSchema),
            new ControlParameter("stagingSchema", draft.StagingSchema),
            new ControlParameter("updatedUtc", now),
            new ControlParameter("connectionId", connectionId.ToString()),
            new ControlParameter("version", version)
        );
        if (affected != 1)
            throw new InvalidOperationException("Connection profile version does not match.");
        transaction.Commit();
        return Task.FromResult(
            new ConnectionProfile(
                connectionId,
                draft.DisplayName,
                draft.ProviderId,
                draft.SecretReference,
                draft.BusinessSchema,
                draft.StagingSchema,
                existing.Version + 1
            )
        );
    }

    public Task DeleteAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        using var transaction = db.BeginTransaction();
        var affected = db.Execute(
            "DELETE FROM ConnectionProfiles WHERE ConnectionId = @connectionId AND Version = @version",
            new ControlParameter("connectionId", connectionId.ToString()),
            new ControlParameter("version", ParseEtag(ifMatch))
        );
        if (affected != 1)
            throw new InvalidOperationException("Connection profile version does not match.");
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<ConnectionProfile> GetProfileAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        return Task.FromResult(ToProfile(GetRow(db, connectionId)));
    }

    public Task<ConnectionProfileSummary> SaveAssessmentAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        ConnectionAssessment assessment,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        using var transaction = db.BeginTransaction();
        var profile = GetRow(db, connectionId);
        var available = JsonSerializer.Serialize(
            assessment
                .Available.Select(capability => capability.ToString())
                .OrderBy(capability => capability, StringComparer.Ordinal)
        );
        var failureCode = SafeFailureCode(assessment.CleanupFailureCode);
        db.Execute(
            "UPDATE ConnectionProfiles SET HealthState = @healthState, AssessmentMode = @assessmentMode, AssessmentRole = @assessmentRole, DatabaseIdentity = @databaseIdentity, ProviderVersion = @providerVersion, CapabilitiesJson = @capabilitiesJson, CleanupFailureCode = @cleanupFailureCode, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE ConnectionId = @connectionId",
            new ControlParameter("healthState", assessment.State.ToString()),
            new ControlParameter("assessmentMode", mode.ToString()),
            new ControlParameter("assessmentRole", role.ToString()),
            new ControlParameter("databaseIdentity", assessment.DatabaseIdentity),
            new ControlParameter("providerVersion", assessment.ProviderVersion),
            new ControlParameter("capabilitiesJson", available),
            new ControlParameter("cleanupFailureCode", failureCode),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("connectionId", connectionId.ToString())
        );
        transaction.Commit();
        EmitAssessment(profile, assessment.State, assessment.Available, failureCode);
        return Task.FromResult(
            ToSummary(profile with { Version = profile.Version + 1, HealthState = assessment.State.ToString() })
        );
    }

    public Task MarkCheckingAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.OpenNative();
        var affected = db.Execute(
            "UPDATE ConnectionProfiles SET HealthState = @healthState, AssessmentMode = @assessmentMode, AssessmentRole = @assessmentRole, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE ConnectionId = @connectionId",
            new ControlParameter("healthState", ConnectionHealthState.Checking.ToString()),
            new ControlParameter("assessmentMode", mode.ToString()),
            new ControlParameter("assessmentRole", role.ToString()),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("connectionId", connectionId.ToString())
        );
        if (affected != 1)
            throw new InvalidOperationException("Connection profile was not found.");
        return Task.CompletedTask;
    }

    private void EmitAssessment(
        Row profile,
        ConnectionHealthState state,
        IEnumerable<ConnectionCapability> available,
        string? failureCode
    )
    {
        var capabilities = string.Join(
            ',',
            available
                .Select(capability => capability.ToString())
                .OrderBy(capability => capability, StringComparer.Ordinal)
        );
        _logger.LogInformation(
            "Connection profile assessed {ConnectionId} {ProviderId} {State} {Capabilities} {ErrorCode}",
            profile.ConnectionId,
            profile.ProviderId,
            state,
            capabilities,
            failureCode
        );
        using var activity = ActivitySource.StartActivity("connection.profile.assessed");
        activity?.SetTag("connection.id", profile.ConnectionId);
        activity?.SetTag("provider.id", profile.ProviderId);
        activity?.SetTag("health.state", state.ToString());
        activity?.SetTag("capabilities", capabilities);
        activity?.SetTag("error.code", failureCode);
    }

    private static Row GetRow(ControlConnection db, Guid connectionId) =>
        db.Single(
            SelectColumns + " WHERE ConnectionId = @connectionId",
            Map,
            new ControlParameter("connectionId", connectionId.ToString())
        ) ?? throw new InvalidOperationException("Connection profile was not found.");

    private static Row Map(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8)
        );

    private static ConnectionProfile ToProfile(Row row) =>
        new(
            Guid.Parse(row.ConnectionId),
            row.DisplayName,
            row.ProviderId,
            new(Enum.Parse<SecretReferenceKind>(row.SecretReferenceKind), row.SecretReferenceLocator),
            row.BusinessSchema,
            row.StagingSchema,
            row.Version
        );

    private static ConnectionProfileSummary ToSummary(Row row) =>
        new(
            Guid.Parse(row.ConnectionId),
            row.DisplayName,
            row.ProviderId,
            Enum.Parse<SecretReferenceKind>(row.SecretReferenceKind),
            Enum.Parse<ConnectionHealthState>(row.HealthState),
            Etag(row.Version)
        );

    private static string Etag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static long ParseEtag(string value) =>
        long.TryParse(value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw new InvalidOperationException("Connection profile version does not match.");

    private static string? SafeFailureCode(string? value) =>
        value is null ? null
        : value.Equals("staging_cleanup_failed", StringComparison.Ordinal) ? value
        : "connection_failed";

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private sealed record Row(
        string ConnectionId,
        string DisplayName,
        string ProviderId,
        string SecretReferenceKind,
        string SecretReferenceLocator,
        string BusinessSchema,
        string StagingSchema,
        long Version,
        string HealthState
    );
}
