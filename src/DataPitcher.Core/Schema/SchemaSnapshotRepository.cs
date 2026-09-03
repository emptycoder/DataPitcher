namespace DataPitcher.Core.Schema;

public enum SchemaScanState
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed record SchemaScan(
    Guid ScanId,
    Guid ConnectionId,
    SchemaScanState State,
    Guid? SnapshotId,
    string? SnapshotHash,
    string? FailureCode
);

public interface ISchemaSnapshotRepository
{
    Task<SchemaScan> QueueAsync(Guid connectionId, string idempotencyKey, CancellationToken cancellationToken);

    Task<SchemaScan> GetScanAsync(Guid connectionId, Guid scanId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredSchemaSnapshot>> ListAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<SchemaScan?> FindScanAsync(Guid scanId, CancellationToken cancellationToken);

    Task<StoredSchemaSnapshot> GetAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);

    Task<StoredSchemaSnapshot?> FindAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);

    Task<StoredSchemaSnapshot?> FindByHashAsync(Guid connectionId, string hash, CancellationToken cancellationToken);

    Task<SchemaGraphProjection> GetGraphAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);

    Task<SchemaTableProjection> GetTableAsync(
        Guid connectionId,
        Guid snapshotId,
        string schema,
        string table,
        CancellationToken cancellationToken
    );

    Task<SchemaNeighbourhoodProjection> GetNeighbourhoodAsync(
        Guid connectionId,
        Guid snapshotId,
        string schema,
        string table,
        int depth,
        CancellationToken cancellationToken
    );

    Task<StoredSchemaSnapshot?> GetLatestAsync(CancellationToken cancellationToken);

    Task<SchemaScan?> ClaimNextAsync(CancellationToken cancellationToken);

    Task CompleteAsync(SchemaScan scan, SchemaSnapshotContent content, CancellationToken cancellationToken);

    Task FailAsync(Guid scanId, string failureCode, CancellationToken cancellationToken);
}
