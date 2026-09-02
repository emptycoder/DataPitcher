using System.Collections.Frozen;
using DataPitcher.Core.Plans;

namespace DataPitcher.Core.Connections;

public enum SecretReferenceKind { EnvironmentVariable, FileMounted }
public enum ConnectionRole { Source, Target }
public enum ConnectionHealthState { Unknown, Checking, Healthy, Degraded, Unhealthy }
public enum ConnectionCapability
{
    CanConnect, CanReadSchema, CanReadBusinessRows, CanCreateSourceStaging,
    CanDropSourceStaging, CanCreateTargetStaging, CanDropTargetStaging,
    CanBulkInsert, CanPreserveIdentity, CanUseTransactions, CanUseSnapshotIsolation,
    CanDeferConstraints, CanDisableConstraints, CanRevalidateConstraints,
    CanFireTriggers, CanSuppressTriggers, CanReseedGeneratedKeys,
    CanUseServerSideTransfer, SupportsDurableResume,
}

public sealed record SecretReference
{
    public SecretReference(SecretReferenceKind kind, string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        if (kind is SecretReferenceKind.FileMounted && !Path.IsPathFullyQualified(locator))
            throw new ArgumentException("Mounted secret references must be absolute.", nameof(locator));
        Kind = kind;
        Locator = locator;
    }

    public SecretReferenceKind Kind { get; }
    public string Locator { get; }
}

public sealed record ConnectionProfile(
    Guid ConnectionId, string DisplayName, string ProviderId, SecretReference SecretReference,
    string BusinessSchema, string StagingSchema, long Version);

public sealed record ConnectionProfileSummary(
    Guid ConnectionId, string DisplayName, string ProviderId, SecretReferenceKind SecretReferenceKind,
    ConnectionHealthState Health, string ETag);

public sealed class ConnectionRequirements
{
    public ConnectionRequirements(IEnumerable<ConnectionCapability> required, IEnumerable<ConnectionCapability> optional)
    {
        Required = required.ToFrozenSet();
        Optional = optional.ToFrozenSet();
    }

    public IReadOnlySet<ConnectionCapability> Required { get; }
    public IReadOnlySet<ConnectionCapability> Optional { get; }

    public static ConnectionRequirements For(TransferMode mode, ConnectionRole role)
    {
        var required = new HashSet<ConnectionCapability>
        {
            ConnectionCapability.CanConnect,
            ConnectionCapability.CanReadSchema,
            ConnectionCapability.CanReadBusinessRows,
            ConnectionCapability.CanUseTransactions,
        };
        var optional = new HashSet<ConnectionCapability> { ConnectionCapability.CanUseSnapshotIsolation };
        if (role is ConnectionRole.Target)
        {
            required.Add(ConnectionCapability.CanBulkInsert);
            required.Add(ConnectionCapability.CanPreserveIdentity);
        }
        if (mode is TransferMode.ServerSide)
            required.Add(ConnectionCapability.CanUseServerSideTransfer);
        if (mode is TransferMode.ResumableStaged && role is ConnectionRole.Source)
        {
            optional.Add(ConnectionCapability.CanCreateSourceStaging);
            optional.Add(ConnectionCapability.CanDropSourceStaging);
            optional.Add(ConnectionCapability.SupportsDurableResume);
        }
        if (mode is TransferMode.ResumableStaged && role is ConnectionRole.Target)
        {
            required.Add(ConnectionCapability.CanCreateTargetStaging);
            required.Add(ConnectionCapability.CanDropTargetStaging);
        }
        return new ConnectionRequirements(required, optional);
    }
}

public sealed class ConnectionProbeEvidence
{
    public ConnectionProbeEvidence(string databaseIdentity, string providerVersion,
        IEnumerable<ConnectionCapability> available, string? cleanupFailureCode)
    {
        DatabaseIdentity = databaseIdentity;
        ProviderVersion = providerVersion;
        Available = available.ToFrozenSet();
        CleanupFailureCode = cleanupFailureCode;
    }

    public string DatabaseIdentity { get; }
    public string ProviderVersion { get; }
    public IReadOnlySet<ConnectionCapability> Available { get; }
    public string? CleanupFailureCode { get; }
}

public sealed class ConnectionAssessment
{
    public ConnectionAssessment(ConnectionHealthState state, string databaseIdentity, string providerVersion,
        IEnumerable<ConnectionCapability> available, IEnumerable<ConnectionCapability> missingRequired,
        IEnumerable<ConnectionCapability> missingOptional, string? cleanupFailureCode)
    {
        State = state;
        DatabaseIdentity = databaseIdentity;
        ProviderVersion = providerVersion;
        Available = available.ToFrozenSet();
        MissingRequired = missingRequired.ToFrozenSet();
        MissingOptional = missingOptional.ToFrozenSet();
        CleanupFailureCode = cleanupFailureCode;
    }

    public ConnectionHealthState State { get; }
    public string DatabaseIdentity { get; }
    public string ProviderVersion { get; }
    public IReadOnlySet<ConnectionCapability> Available { get; }
    public IReadOnlySet<ConnectionCapability> MissingRequired { get; }
    public IReadOnlySet<ConnectionCapability> MissingOptional { get; }
    public string? CleanupFailureCode { get; }
}

public static class ConnectionHealthClassifier
{
    public static ConnectionAssessment Classify(ConnectionRequirements requirements, ConnectionProbeEvidence evidence)
    {
        var missingRequired = requirements.Required.Where(capability => !evidence.Available.Contains(capability)).ToHashSet();
        var missingOptional = requirements.Optional.Where(capability => !evidence.Available.Contains(capability)).ToHashSet();
        var state = evidence.CleanupFailureCode is not null || missingRequired.Count != 0
            ? ConnectionHealthState.Unhealthy
            : missingOptional.Count != 0 ? ConnectionHealthState.Degraded : ConnectionHealthState.Healthy;
        return new ConnectionAssessment(state, evidence.DatabaseIdentity, evidence.ProviderVersion,
            evidence.Available, missingRequired, missingOptional, evidence.CleanupFailureCode);
    }
}
