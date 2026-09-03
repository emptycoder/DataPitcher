namespace DataPitcher.Core.Selection;

public sealed record SelectionRecord(
    Guid SelectionId,
    string DisplayName,
    string QueryJson,
    long Version,
    DateTimeOffset UpdatedUtc,
    Guid? ConnectionId = null,
    Guid? SnapshotId = null,
    string? RootSchema = null,
    string? RootTable = null,
    string? StableKeyConstraintName = null,
    IReadOnlyList<string>? StableKeyColumns = null
);

public sealed class SelectionVersionMismatchException : InvalidOperationException
{
    public SelectionVersionMismatchException()
        : base("Selection version does not match.") { }
}

public interface ISelectionRepository
{
    Task<SelectionRecord> SaveAsync(
        Guid selectionId,
        string displayName,
        string queryJson,
        string ifMatch,
        CancellationToken cancellationToken,
        Guid? connectionId = null,
        Guid? snapshotId = null,
        string? rootSchema = null,
        string? rootTable = null,
        string? stableKeyConstraintName = null,
        IReadOnlyList<string>? stableKeyColumns = null
    );

    Task DeleteAsync(Guid selectionId, string ifMatch, CancellationToken cancellationToken);

    Task<SelectionRecord?> FindAsync(Guid selectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SelectionRecord>> ListAsync(CancellationToken cancellationToken);
}
