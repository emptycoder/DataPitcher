namespace DataPitcher.Api.Contracts;

public sealed record CreateConnectionRequest(string DisplayName, string ProviderId, Guid CredentialId, string IfMatch);
public sealed record ConnectionResponse(Guid ConnectionId, string DisplayName, string ProviderId, string Health, string ETag);
public sealed record OperationReceiptResponse(Guid OperationId, string State, Uri StatusUri, Guid? ConnectionId, Guid? PlanId, Guid? JobId);
public sealed record ProviderResponse(string ProviderId, string DisplayName);
public sealed record ResourceIdentifiers(Guid? ConnectionId, Guid? SnapshotId, Guid? SelectionId, Guid? PlanId, Guid? JobId);

public sealed record SchemaSnapshotResponse(Guid ConnectionId, Guid SnapshotId, string Hash, DateTimeOffset CapturedAtUtc);
public sealed record SaveSelectionRequest(string DisplayName, string QueryJson, string IfMatch);
public sealed record SelectionResponse(Guid SelectionId, long Version, string ETag);
public sealed record SavePlanRequest(string DisplayName, string? OperatorNote, string IfMatch);
public sealed record PlanResponse(Guid PlanId, int Version, string? CanonicalHash, string ETag);
public enum JobCommand { Pause, Resume, Cancel }
public sealed record JobResponse(Guid JobId, Guid PlanId, string State, long RowsTransferred, long BytesTransferred);
