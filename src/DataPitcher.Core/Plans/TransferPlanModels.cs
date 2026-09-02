using DataPitcher.Core.Closure;
namespace DataPitcher.Core.Plans;
public enum PlanTableState { Root, RequiredDependency, ExplicitDependent, TargetSatisfied, Excluded, Blocked, Conflict, CycleMember }
public enum RelationshipDirection { Outbound, Inbound }
public enum ConsistencyMode { FrozenKeys, RepeatableReadRun }
public enum TransferMode { DirectFast, ResumableStaged, ServerSide }
public enum CycleStrategy { NotApplicable, DeferredConstraints, NullableForeignKeyTwoPhase, SuspendAndRevalidateConstraints, Blocked }
public enum TriggerStrategy { Fire, Suppress }
public enum ConstraintStrategy { Enforce, Defer, DisableAndRevalidate }
public enum VerificationStrategy { Standard, StrictExact }
public sealed record TableAddress(string Schema, string Name);
public sealed record ConnectionFingerprint(string Provider, string DatabaseIdentity, string Fingerprint);
public sealed record SchemaSnapshotReference(string Hash);
public sealed record SelectionReference(Guid SelectionId, long Version, string ParameterHash);
public sealed class RelationshipPolicy
{
    public RelationshipPolicy(string name, TableAddress from, TableAddress to, IEnumerable<string> fromColumns, IEnumerable<string> toColumns, RelationshipDirection direction, bool isEnabled)
    { Name = name; From = from; To = to; FromColumns = Array.AsReadOnly(fromColumns.ToArray()); ToColumns = Array.AsReadOnly(toColumns.ToArray()); Direction = direction; IsEnabled = isEnabled; }
    public string Name { get; } public TableAddress From { get; } public TableAddress To { get; } public IReadOnlyList<string> FromColumns { get; } public IReadOnlyList<string> ToColumns { get; } public RelationshipDirection Direction { get; } public bool IsEnabled { get; }
}
public sealed record TableConflictPolicy(TableAddress Table, RootConflictPolicy Policy);
public sealed class StableKeyDefinition
{
    public StableKeyDefinition(TableAddress table, string constraintName, IEnumerable<string> columns) { Table = table; ConstraintName = constraintName; Columns = Array.AsReadOnly(columns.ToArray()); }
    public TableAddress Table { get; } public string ConstraintName { get; } public IReadOnlyList<string> Columns { get; }
}
public sealed record ColumnMapping(string Source, string Target);
public sealed class TableMapping
{
    public TableMapping(TableAddress source, TableAddress target, IEnumerable<ColumnMapping> columns) { Source = source; Target = target; Columns = Array.AsReadOnly(columns.ToArray()); }
    public TableAddress Source { get; } public TableAddress Target { get; } public IReadOnlyList<ColumnMapping> Columns { get; }
}
public sealed record ManifestCounts(long Included, long PlannedWrites, long Inserts, long Updates);
public sealed class TopologicalGroup
{
    public TopologicalGroup(IEnumerable<TableAddress> tables) => Tables = Array.AsReadOnly(tables.ToArray());
    public IReadOnlyList<TableAddress> Tables { get; }
}
public sealed class PlanTable
{
    public PlanTable(TableMapping mapping, PlanTableState state, ManifestCounts manifest, TopologicalGroup topologicalGroup, CycleStrategy cycleStrategy) { Mapping = mapping; State = state; Manifest = manifest; TopologicalGroup = topologicalGroup; CycleStrategy = cycleStrategy; }
    public TableMapping Mapping { get; } public PlanTableState State { get; } public ManifestCounts Manifest { get; } public TopologicalGroup TopologicalGroup { get; } public CycleStrategy CycleStrategy { get; }
}
public sealed class TransferPlanContent
{
    public TransferPlanContent(ConnectionFingerprint source, ConnectionFingerprint target, SchemaSnapshotReference sourceSchema, SchemaSnapshotReference targetSchema, IEnumerable<SelectionReference> selections, IEnumerable<RelationshipPolicy> relationships, IEnumerable<TableConflictPolicy> conflictPolicies, ConsistencyMode consistencyMode, TransferMode transferMode, TriggerStrategy triggerStrategy, ConstraintStrategy constraintStrategy, IEnumerable<StableKeyDefinition> stableKeys, IEnumerable<PlanTable> tables, BatchTarget batchTarget, VerificationStrategy verificationStrategy, ManifestCounts manifestTotals)
    { Source = source; Target = target; SourceSchema = sourceSchema; TargetSchema = targetSchema; Selections = Array.AsReadOnly(selections.ToArray()); Relationships = Array.AsReadOnly(relationships.ToArray()); ConflictPolicies = Array.AsReadOnly(conflictPolicies.ToArray()); ConsistencyMode = consistencyMode; TransferMode = transferMode; TriggerStrategy = triggerStrategy; ConstraintStrategy = constraintStrategy; StableKeys = Array.AsReadOnly(stableKeys.ToArray()); Tables = Array.AsReadOnly(tables.ToArray()); BatchTarget = batchTarget; VerificationStrategy = verificationStrategy; ManifestTotals = manifestTotals; }
    public ConnectionFingerprint Source { get; } public ConnectionFingerprint Target { get; } public SchemaSnapshotReference SourceSchema { get; } public SchemaSnapshotReference TargetSchema { get; } public IReadOnlyList<SelectionReference> Selections { get; } public IReadOnlyList<RelationshipPolicy> Relationships { get; } public IReadOnlyList<TableConflictPolicy> ConflictPolicies { get; } public ConsistencyMode ConsistencyMode { get; } public TransferMode TransferMode { get; } public TriggerStrategy TriggerStrategy { get; } public ConstraintStrategy ConstraintStrategy { get; } public IReadOnlyList<StableKeyDefinition> StableKeys { get; } public IReadOnlyList<PlanTable> Tables { get; } public BatchTarget BatchTarget { get; } public VerificationStrategy VerificationStrategy { get; } public ManifestCounts ManifestTotals { get; }
}
public sealed record BatchTarget(int MaximumRows, int MaximumBytes);
