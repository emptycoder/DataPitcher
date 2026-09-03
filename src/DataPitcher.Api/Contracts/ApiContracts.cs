namespace DataPitcher.Api.Contracts;

public sealed record CreateConnectionRequest(string DisplayName, string ProviderId, Guid CredentialId, string IfMatch);
public sealed record ConnectionResponse(Guid ConnectionId, string DisplayName, string ProviderId, string Health, string ETag);
public sealed record OperationReceiptResponse(Guid OperationId, string State, Uri StatusUri, Guid? ConnectionId, Guid? PlanId, Guid? JobId);
public sealed record ProviderResponse(string ProviderId, string DisplayName);
public sealed record ResourceIdentifiers(Guid? ConnectionId, Guid? SnapshotId, Guid? SelectionId, Guid? PlanId, Guid? JobId);

public sealed record SchemaSnapshotSummaryResponse(Guid SnapshotId, string Hash, DateTimeOffset CapturedAtUtc);
public sealed record SchemaSnapshotAddressResponse(string Schema, string Name);
public sealed record SchemaSnapshotColumnResponse(string Name, string StoreType, bool IsNullable);
public sealed record SchemaSnapshotKeyResponse(string Name, IReadOnlyList<string> Columns);
public sealed record SchemaSnapshotTableResponse(string Schema, string Name, IReadOnlyList<SchemaSnapshotColumnResponse> Columns, SchemaSnapshotKeyResponse? PrimaryKey);
public sealed record SchemaSnapshotForeignKeyResponse(string Name, SchemaSnapshotAddressResponse ChildTable, SchemaSnapshotAddressResponse ParentTable, IReadOnlyList<string> ChildColumns, IReadOnlyList<string> ParentColumns, bool IsEnforced, bool IsTrusted);
public sealed record SchemaSnapshotResponse(Guid ConnectionId, Guid SnapshotId, string Hash, DateTimeOffset CapturedAtUtc)
{
    public IReadOnlyList<SchemaSnapshotTableResponse> Tables { get; init; } = [];
    public IReadOnlyList<SchemaSnapshotForeignKeyResponse> ForeignKeys { get; init; } = [];
}
public sealed record SaveSelectionRequest(string DisplayName, string QueryJson, string IfMatch);
public sealed record SelectionResponse(Guid SelectionId, long Version, string ETag);
public sealed record SavePlanRequest(string DisplayName, string? OperatorNote, string IfMatch, Guid? SelectionId = null, Guid? SourceConnectionId = null, Guid? TargetConnectionId = null);
public sealed record PlanResponse(Guid PlanId, int Version, string? CanonicalHash, string ETag);
public sealed record PlanReviewMessageResponse(string Code, string Message);
public sealed record PlanReviewSealResponse(string Status, IReadOnlyList<PlanReviewMessageResponse> InvalidationReasons);
public sealed record PlanReviewTotalsResponse(long Included, long PlannedWrites, long Inserts, long Updates, long EstimatedBytes);
public sealed record PlanReviewPreconditionResponse(string Code, bool Satisfied, string Message);
public sealed record PlanReviewAddressResponse(string Schema, string Name);
public sealed record PlanReviewColumnResponse(string Source, string Target);
public sealed record PlanReviewTableResponse(PlanReviewAddressResponse Source, PlanReviewAddressResponse Target, string State, long TransferOrder, long Included, long PlannedWrites, long Inserts, long Updates, long EstimatedBytes, IReadOnlyList<PlanReviewColumnResponse> Columns);
public sealed record PlanReviewConflictResponse(string Table, string Policy, string Message);
public sealed record PlanReviewCycleResponse(IReadOnlyList<string> Tables, string Strategy, string Message);
public sealed record PlanReviewSelectionResponse(Guid SelectionId, string DisplayName, Guid? ConnectionId, Guid? SnapshotId);
public sealed record PlanReviewResponse(Guid PlanId, int Version, string CanonicalHash, PlanReviewSealResponse Seal, PlanReviewTotalsResponse Totals, IReadOnlyList<PlanReviewPreconditionResponse> StartPreconditions, IReadOnlyList<PlanReviewTableResponse> Tables, IReadOnlyList<PlanReviewConflictResponse> Conflicts, IReadOnlyList<PlanReviewCycleResponse> Cycles, IReadOnlyList<PlanReviewMessageResponse> Warnings, IReadOnlyList<PlanReviewMessageResponse> Blockers, PlanReviewSelectionResponse? Selection = null, ConnectionResponse? Source = null, ConnectionResponse? Target = null);
public sealed record InclusionPathRequest(string Table, string StableKey);
public sealed record InclusionPathStepResponse(string Relationship, string From, string To, string Reason);
public sealed record InclusionPathResponse(string Table, string StableKey, string RootSelection, IReadOnlyList<InclusionPathStepResponse> Steps);
public enum JobCommand { Pause, Resume, Cancel }
public sealed record JobResponse(Guid JobId, Guid PlanId, string State, long RowsTransferred, long BytesTransferred);
