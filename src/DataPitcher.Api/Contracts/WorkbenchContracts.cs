namespace DataPitcher.Api.Contracts;

public sealed record PlanSchemaDependencyGraphTableResponse(string Id, string Schema, string Name, string ComponentId, string State);
public sealed record PlanSchemaDependencyGraphRelationshipResponse(string Id, string Name, string ChildTableId, string ParentTableId);
public sealed record PlanSchemaDependencyGraphResponse(
    string Revision,
    IReadOnlyList<string> PlannedTableIds,
    IReadOnlyList<PlanSchemaDependencyGraphTableResponse> Tables,
    IReadOnlyList<PlanSchemaDependencyGraphRelationshipResponse> Relationships);

public sealed record SelectionColumnResponse(string Name, string ValueKind);
public sealed record SelectionTableResponse(string TableId, string SchemaName, string TableName, long? ApproximateRowCount, IReadOnlyList<string>? StableKeyColumns, IReadOnlyList<SelectionColumnResponse> Columns);
public sealed record ForeignKeyPathResponse(string ForeignKeyId, string ChildTableId, string ParentTableId);
public sealed record SelectionWorkbenchSchemaResponse(IReadOnlyList<SelectionTableResponse> Tables, IReadOnlyList<ForeignKeyPathResponse> ForeignKeys, string SchemaRevision);

public sealed record TypedParameterValueRequest(string Name, string Kind, object Value);
public sealed record SelectionRequestBody(string Mode, object? Visual, string? RawSql, IReadOnlyList<TypedParameterValueRequest> Parameters, string SchemaRevision, Guid? ConnectionId = null, Guid? SnapshotId = null);

public sealed record TypedParameterDefinitionResponse(string Name, string Kind);
public sealed record CompilationResponse(string SqlSnapshot, IReadOnlyList<TypedParameterDefinitionResponse> Parameters, IReadOnlyList<string> Warnings, string SchemaRevision);
public sealed record PreviewResponse(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool HasMore, string Revision);
public sealed record CountResponse(long DistinctStableKeyCount);
public sealed record SavedSelectionResponse(Guid SelectionId, string DisplayName, long Version, string ETag, string Mode, IReadOnlyList<string> Warnings);
public sealed record ListSelectionsResponse(IReadOnlyList<SavedSelectionResponse> Selections);

/// <summary>
/// Live selection execution (visual/raw SQL compilation, preview, and row counting against a source connection)
/// requires an AST-to-domain mapper and a per-request provider execution context that do not exist in this
/// codebase yet. This exception marks that gap explicitly rather than fabricating a result.
/// </summary>
public sealed class SelectionExecutionNotWiredException() : InvalidOperationException(
    "Live selection compilation, preview, and count execution are not yet wired to a source connection.");
