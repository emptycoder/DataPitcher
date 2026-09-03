using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.Sqlite;

namespace DataPitcher.ControlStore;

public sealed class SchemaSnapshotStore(ControlDatabase database, IClock clock) : ISchemaSnapshotRepository
{
    private const string ScanSelect =
        "SELECT ScanId, ConnectionId, IdempotencyKey, State, SnapshotId, SnapshotHash, FailureCode, CreatedUtc, UpdatedUtc FROM SchemaScans";

    private const string SnapshotSelect =
        "SELECT SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc FROM SchemaSnapshots";

    public Task<SchemaScan> QueueAsync(Guid connectionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var row = new ScanRecord(
            Guid.NewGuid().ToString(),
            connectionId.ToString(),
            idempotencyKey,
            SchemaScanState.Queued.ToString(),
            null,
            null,
            null,
            Stamp(clock.UtcNow),
            Stamp(clock.UtcNow)
        );
        var inserted = db.Execute(
            "INSERT OR IGNORE INTO SchemaScans (ScanId, ConnectionId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES (@scanId, @connectionId, @idempotencyKey, @state, @createdUtc, @updatedUtc)",
            new ControlParameter("scanId", row.ScanId),
            new ControlParameter("connectionId", row.ConnectionId),
            new ControlParameter("idempotencyKey", row.IdempotencyKey),
            new ControlParameter("state", row.State),
            new ControlParameter("createdUtc", row.CreatedUtc),
            new ControlParameter("updatedUtc", row.UpdatedUtc)
        );
        if (inserted == 0)
            return Task.FromResult(
                ToScan(
                    db.Single(
                        ScanSelect + " WHERE ConnectionId = @connectionId AND IdempotencyKey = @idempotencyKey",
                        ReadScan,
                        new ControlParameter("connectionId", row.ConnectionId),
                        new ControlParameter("idempotencyKey", idempotencyKey)
                    ) ?? throw new InvalidOperationException("Sequence contains no elements")
                )
            );
        transaction.Commit();
        return Task.FromResult(ToScan(row));
    }

    public Task<SchemaScan> GetScanAsync(Guid connectionId, Guid scanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        return Task.FromResult(
            ToScan(
                db.Single(
                    ScanSelect + " WHERE ConnectionId = @connectionId AND ScanId = @scanId",
                    ReadScan,
                    new ControlParameter("connectionId", connectionId.ToString()),
                    new ControlParameter("scanId", scanId.ToString())
                ) ?? throw new InvalidOperationException("Schema scan was not found.")
            )
        );
    }

    public Task<IReadOnlyList<StoredSchemaSnapshot>> ListAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        IReadOnlyList<StoredSchemaSnapshot> snapshots = db.Query(
                SnapshotSelect + " WHERE ConnectionId = @connectionId ORDER BY CreatedUtc DESC, SnapshotId ASC",
                ReadSnapshot,
                new ControlParameter("connectionId", connectionId.ToString())
            )
            .Select(ToSnapshot)
            .ToArray();
        return Task.FromResult(snapshots);
    }

    public Task<SchemaScan?> FindScanAsync(Guid scanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var scan = db.Single(
            ScanSelect + " WHERE ScanId = @scanId",
            ReadScan,
            new ControlParameter("scanId", scanId.ToString())
        );
        return Task.FromResult(scan is null ? null : ToScan(scan));
    }

    public Task<StoredSchemaSnapshot> GetAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row =
            db.Single(
                SnapshotSelect + " WHERE ConnectionId = @connectionId AND SnapshotId = @snapshotId",
                ReadSnapshot,
                new ControlParameter("connectionId", connectionId.ToString()),
                new ControlParameter("snapshotId", snapshotId.ToString())
            ) ?? throw new InvalidOperationException("Schema snapshot was not found.");
        return Task.FromResult(ToSnapshot(row));
    }

    public Task<StoredSchemaSnapshot?> FindAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.Single(
            SnapshotSelect + " WHERE ConnectionId = @connectionId AND SnapshotId = @snapshotId",
            ReadSnapshot,
            new ControlParameter("connectionId", connectionId.ToString()),
            new ControlParameter("snapshotId", snapshotId.ToString())
        );
        return Task.FromResult(row is null ? null : ToSnapshot(row));
    }

    public Task<bool> DeleteAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var selections = db.Scalar<long>(
            "SELECT COUNT(*) FROM Selections WHERE SnapshotId = @snapshotId",
            new ControlParameter("snapshotId", snapshotId.ToString())
        );
        if (selections > 0)
            throw new SchemaSnapshotInUseException(checked((int)selections));
        var affected = db.Execute(
            "DELETE FROM SchemaSnapshots WHERE ConnectionId = @connectionId AND SnapshotId = @snapshotId",
            new ControlParameter("connectionId", connectionId.ToString()),
            new ControlParameter("snapshotId", snapshotId.ToString())
        );
        transaction.Commit();
        return Task.FromResult(affected == 1);
    }

    public Task<StoredSchemaSnapshot?> FindByHashAsync(
        Guid connectionId,
        string hash,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.Query(
                SnapshotSelect
                    + " WHERE ConnectionId = @connectionId AND SnapshotHash = @snapshotHash ORDER BY CreatedUtc ASC, SnapshotId ASC LIMIT 1",
                ReadSnapshot,
                new ControlParameter("connectionId", connectionId.ToString()),
                new ControlParameter("snapshotHash", hash)
            )
            .FirstOrDefault();
        return Task.FromResult(row is null ? null : ToSnapshot(row));
    }

    public async Task<SchemaGraphProjection> GetGraphAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        return new SchemaGraphProjection(Addresses(snapshot.Content.Tables), Edges(snapshot.Content.ForeignKeys));
    }

    public async Task<SchemaTableProjection> GetTableAsync(
        Guid connectionId,
        Guid snapshotId,
        string schema,
        string table,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        var selected =
            snapshot.Content.Tables.SingleOrDefault(item =>
                string.Equals(item.Schema, schema, StringComparison.Ordinal)
                && string.Equals(item.Name, table, StringComparison.Ordinal)
            ) ?? throw new InvalidOperationException("Schema table was not found.");
        return new SchemaTableProjection(
            selected,
            snapshot
                .Content.ForeignKeys.Where(item =>
                    Same(item.ChildTable, schema, table) || Same(item.ParentTable, schema, table)
                )
                .OrderBy(item => item.Name, StringComparer.Ordinal)
        );
    }

    public async Task<SchemaNeighbourhoodProjection> GetNeighbourhoodAsync(
        Guid connectionId,
        Guid snapshotId,
        string schema,
        string table,
        int depth,
        CancellationToken cancellationToken
    )
    {
        if (depth < 1)
            throw new ArgumentOutOfRangeException(nameof(depth));
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        var center =
            snapshot
                .Content.Tables.Select(item => new SchemaTableAddress(item.Schema, item.Name))
                .SingleOrDefault(item => Same(item, schema, table))
            ?? throw new InvalidOperationException("Schema table was not found.");
        var included = new HashSet<SchemaTableAddress> { center };
        var frontier = new HashSet<SchemaTableAddress> { center };
        for (var currentDepth = 0; currentDepth < depth; currentDepth++)
        {
            var next = new HashSet<SchemaTableAddress>();
            foreach (var foreignKey in snapshot.Content.ForeignKeys)
            {
                if (frontier.Contains(foreignKey.ChildTable))
                    next.Add(foreignKey.ParentTable);
                if (frontier.Contains(foreignKey.ParentTable))
                    next.Add(foreignKey.ChildTable);
            }
            next.ExceptWith(included);
            included.UnionWith(next);
            frontier = next;
        }
        return new SchemaNeighbourhoodProjection(
            center,
            depth,
            included
                .OrderBy(item => item.Schema, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal),
            snapshot
                .Content.ForeignKeys.Where(item =>
                    included.Contains(item.ChildTable) && included.Contains(item.ParentTable)
                )
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new SchemaGraphEdge(item.ChildTable, item.ParentTable, item.Name))
        );
    }

    public Task<StoredSchemaSnapshot?> GetLatestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.Query(SnapshotSelect + " ORDER BY CreatedUtc DESC, rowid ASC LIMIT 1", ReadSnapshot)
            .FirstOrDefault();
        if (row is null)
            return Task.FromResult<StoredSchemaSnapshot?>(null);
        return Task.FromResult<StoredSchemaSnapshot?>(ToSnapshot(row));
    }

    public Task<SchemaScan?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var row = db.Query(
                ScanSelect + " WHERE State = @queued ORDER BY CreatedUtc ASC, ScanId ASC LIMIT 1",
                ReadScan,
                new ControlParameter("queued", SchemaScanState.Queued.ToString())
            )
            .FirstOrDefault();
        if (row is null)
            return Task.FromResult<SchemaScan?>(null);
        var affected = db.Execute(
            "UPDATE SchemaScans SET State = @state, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @queued",
            new ControlParameter("state", SchemaScanState.Running.ToString()),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("scanId", row.ScanId),
            new ControlParameter("queued", SchemaScanState.Queued.ToString())
        );
        if (affected != 1)
            return Task.FromResult<SchemaScan?>(null);
        transaction.Commit();
        return Task.FromResult<SchemaScan?>(ToScan(row with { State = SchemaScanState.Running.ToString() }));
    }

    public Task CompleteAsync(SchemaScan scan, SchemaSnapshotContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var snapshot = new SnapshotRecord(
            Guid.NewGuid().ToString(),
            scan.ConnectionId.ToString(),
            CanonicalSchemaSnapshotHasher.Hash(content),
            JsonSerializer.Serialize(ToRow(content)),
            Stamp(clock.UtcNow)
        );
        db.Execute(
            "INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)",
            new ControlParameter("snapshotId", snapshot.SnapshotId),
            new ControlParameter("connectionId", snapshot.ConnectionId),
            new ControlParameter("snapshotHash", snapshot.SnapshotHash),
            new ControlParameter("contentJson", snapshot.ContentJson),
            new ControlParameter("createdUtc", snapshot.CreatedUtc)
        );
        db.Execute(
            "UPDATE SchemaScans SET State = @state, SnapshotId = @snapshotId, SnapshotHash = @snapshotHash, FailureCode = NULL, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @running",
            new ControlParameter("state", SchemaScanState.Completed.ToString()),
            new ControlParameter("snapshotId", snapshot.SnapshotId),
            new ControlParameter("snapshotHash", snapshot.SnapshotHash),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("scanId", scan.ScanId.ToString()),
            new ControlParameter("running", SchemaScanState.Running.ToString())
        );
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid scanId, string failureCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        db.Execute(
            "UPDATE SchemaScans SET State = @state, FailureCode = @failureCode, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @running",
            new ControlParameter("state", SchemaScanState.Failed.ToString()),
            new ControlParameter("failureCode", failureCode),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("scanId", scanId.ToString()),
            new ControlParameter("running", SchemaScanState.Running.ToString())
        );
        return Task.CompletedTask;
    }

    private static ScanRecord ReadScan(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8)
        );

    private static SnapshotRecord ReadSnapshot(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));

    private static StoredSchemaSnapshot ToSnapshot(SnapshotRecord row)
    {
        var content =
            JsonSerializer.Deserialize<SnapshotRow>(row.ContentJson)
            ?? throw new InvalidOperationException("Schema snapshot is invalid.");
        return new StoredSchemaSnapshot(
            Guid.Parse(row.SnapshotId),
            Guid.Parse(row.ConnectionId),
            row.SnapshotHash,
            DateTimeOffset.Parse(row.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            FromRow(content)
        );
    }

    private static SchemaScan ToScan(ScanRecord row) =>
        new(
            Guid.Parse(row.ScanId),
            Guid.Parse(row.ConnectionId),
            Enum.Parse<SchemaScanState>(row.State),
            row.SnapshotId is null ? null : Guid.Parse(row.SnapshotId),
            row.SnapshotHash,
            row.FailureCode
        );

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static bool Same(SchemaTableAddress address, string schema, string table) =>
        string.Equals(address.Schema, schema, StringComparison.Ordinal)
        && string.Equals(address.Name, table, StringComparison.Ordinal);

    private static IEnumerable<SchemaTableAddress> Addresses(IEnumerable<SchemaTable> tables) =>
        tables
            .Select(table => new SchemaTableAddress(table.Schema, table.Name))
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal);

    private static IEnumerable<SchemaGraphEdge> Edges(IEnumerable<SchemaForeignKey> foreignKeys) =>
        foreignKeys
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new SchemaGraphEdge(item.ChildTable, item.ParentTable, item.Name));

    private static SnapshotRow ToRow(SchemaSnapshotContent content) =>
        new(
            content
                .Tables.Select(table => new TableRow(
                    table.Schema,
                    table.Name,
                    table
                        .Columns.Select(column => new ColumnRow(
                            column.Name,
                            column.StoreType,
                            column.ClrType,
                            column.IsNullable
                        ))
                        .ToArray(),
                    table.PrimaryKey is null ? null : ToRow(table.PrimaryKey),
                    table.UniqueConstraints.Select(ToRow).ToArray()
                ))
                .ToArray(),
            content
                .ForeignKeys.Select(foreignKey => new ForeignKeyRow(
                    foreignKey.Name,
                    ToRow(foreignKey.ChildTable),
                    ToRow(foreignKey.ParentTable),
                    foreignKey.ChildColumns.ToArray(),
                    foreignKey.ParentColumns.ToArray(),
                    foreignKey.IsEnforced,
                    foreignKey.IsTrusted
                ))
                .ToArray(),
            content.DatabaseIdentity,
            content.ProviderVersion
        );

    private static SchemaSnapshotContent FromRow(SnapshotRow row) =>
        new(
            row.Tables.Select(table => new SchemaTable(
                table.Schema,
                table.Name,
                table.Columns.Select(column => new SchemaColumn(
                    column.Name,
                    column.StoreType,
                    column.ClrType,
                    column.IsNullable
                )),
                table.PrimaryKey is null ? null : FromRow(table.PrimaryKey),
                table.UniqueConstraints.Select(FromRow)
            )),
            row.ForeignKeys.Select(foreignKey => new SchemaForeignKey(
                foreignKey.Name,
                FromRow(foreignKey.ChildTable),
                FromRow(foreignKey.ParentTable),
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                foreignKey.IsEnforced,
                foreignKey.IsTrusted
            )),
            row.DatabaseIdentity,
            row.ProviderVersion
        );

    private static KeyRow ToRow(SchemaKey key) => new(key.Name, key.Columns.ToArray());

    private static SchemaKey FromRow(KeyRow row) => new(row.Name, row.Columns);

    private static AddressRow ToRow(SchemaTableAddress address) => new(address.Schema, address.Name);

    private static SchemaTableAddress FromRow(AddressRow row) => new(row.Schema, row.Name);

    private sealed record ScanRecord(
        string ScanId,
        string ConnectionId,
        string IdempotencyKey,
        string State,
        string? SnapshotId,
        string? SnapshotHash,
        string? FailureCode,
        string CreatedUtc,
        string UpdatedUtc
    );

    private sealed record SnapshotRecord(
        string SnapshotId,
        string ConnectionId,
        string SnapshotHash,
        string ContentJson,
        string CreatedUtc
    );

    private sealed record SnapshotRow(
        TableRow[] Tables,
        ForeignKeyRow[] ForeignKeys,
        string DatabaseIdentity,
        string ProviderVersion
    );

    private sealed record TableRow(
        string Schema,
        string Name,
        ColumnRow[] Columns,
        KeyRow? PrimaryKey,
        KeyRow[] UniqueConstraints
    );

    private sealed record ColumnRow(string Name, string StoreType, string ClrType, bool IsNullable);

    private sealed record KeyRow(string Name, string[] Columns);

    private sealed record ForeignKeyRow(
        string Name,
        AddressRow ChildTable,
        AddressRow ParentTable,
        string[] ChildColumns,
        string[] ParentColumns,
        bool IsEnforced,
        bool IsTrusted
    );

    private sealed record AddressRow(string Schema, string Name);
}
