using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Schema;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Schema;

public enum SchemaScanState { Queued, Running, Completed, Failed }
public sealed record SchemaScan(Guid ScanId, Guid ConnectionId, SchemaScanState State, Guid? SnapshotId, string? SnapshotHash, string? FailureCode);

public sealed class SchemaSnapshotStore(ControlDatabase database, IClock clock)
{
    public Task<SchemaScan> QueueAsync(Guid connectionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var row = new SchemaScanRow { ScanId = Guid.NewGuid().ToString(), ConnectionId = connectionId.ToString(), IdempotencyKey = idempotencyKey, State = SchemaScanState.Queued.ToString(), CreatedUtc = Stamp(clock.UtcNow), UpdatedUtc = Stamp(clock.UtcNow) };
        var inserted = db.Execute("INSERT OR IGNORE INTO SchemaScans (ScanId, ConnectionId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES (@scanId, @connectionId, @idempotencyKey, @state, @createdUtc, @updatedUtc)", Parameters(row));
        if (inserted == 0)
            return Task.FromResult(ToScan(db.GetTable<SchemaScanRow>().Single(scan => scan.ConnectionId == row.ConnectionId && scan.IdempotencyKey == idempotencyKey)));
        transaction.Commit();
        return Task.FromResult(ToScan(row));
    }

    public Task<SchemaScan> GetScanAsync(Guid connectionId, Guid scanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        return Task.FromResult(ToScan(db.GetTable<SchemaScanRow>().SingleOrDefault(scan => scan.ConnectionId == connectionId.ToString() && scan.ScanId == scanId.ToString()) ?? throw new InvalidOperationException("Schema scan was not found.")));
    }

    public Task<StoredSchemaSnapshot> GetAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.GetTable<SchemaSnapshotRow>().SingleOrDefault(snapshot => snapshot.ConnectionId == connectionId.ToString() && snapshot.SnapshotId == snapshotId.ToString()) ?? throw new InvalidOperationException("Schema snapshot was not found.");
        var content = JsonSerializer.Deserialize<SnapshotRow>(row.ContentJson) ?? throw new InvalidOperationException("Schema snapshot is invalid.");
        return Task.FromResult(new StoredSchemaSnapshot(snapshotId, connectionId, row.SnapshotHash, DateTimeOffset.Parse(row.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), FromRow(content)));
    }

    public async Task<SchemaGraphProjection> GetGraphAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        return new SchemaGraphProjection(Addresses(snapshot.Content.Tables), Edges(snapshot.Content.ForeignKeys));
    }

    public async Task<SchemaTableProjection> GetTableAsync(Guid connectionId, Guid snapshotId, string schema, string table, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        var selected = snapshot.Content.Tables.SingleOrDefault(item => string.Equals(item.Schema, schema, StringComparison.Ordinal) && string.Equals(item.Name, table, StringComparison.Ordinal)) ?? throw new InvalidOperationException("Schema table was not found.");
        return new SchemaTableProjection(selected, snapshot.Content.ForeignKeys.Where(item => Same(item.ChildTable, schema, table) || Same(item.ParentTable, schema, table)).OrderBy(item => item.Name, StringComparer.Ordinal));
    }

    public async Task<SchemaNeighbourhoodProjection> GetNeighbourhoodAsync(Guid connectionId, Guid snapshotId, string schema, string table, int depth, CancellationToken cancellationToken)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
        var snapshot = await GetAsync(connectionId, snapshotId, cancellationToken);
        var center = snapshot.Content.Tables.Select(item => new SchemaTableAddress(item.Schema, item.Name)).SingleOrDefault(item => Same(item, schema, table)) ?? throw new InvalidOperationException("Schema table was not found.");
        var included = new HashSet<SchemaTableAddress>(AddressComparer.Instance) { center };
        var frontier = new HashSet<SchemaTableAddress>(AddressComparer.Instance) { center };
        for (var currentDepth = 0; currentDepth < depth; currentDepth++)
        {
            var next = new HashSet<SchemaTableAddress>(AddressComparer.Instance);
            foreach (var foreignKey in snapshot.Content.ForeignKeys)
            {
                if (frontier.Contains(foreignKey.ChildTable)) next.Add(foreignKey.ParentTable);
                if (frontier.Contains(foreignKey.ParentTable)) next.Add(foreignKey.ChildTable);
            }
            next.ExceptWith(included);
            included.UnionWith(next);
            frontier = next;
        }
        return new SchemaNeighbourhoodProjection(center, depth, included.OrderBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal), snapshot.Content.ForeignKeys.Where(item => included.Contains(item.ChildTable) && included.Contains(item.ParentTable)).OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new SchemaGraphEdge(item.ChildTable, item.ParentTable, item.Name)));
    }

    internal Task<SchemaScan?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var row = db.GetTable<SchemaScanRow>().Where(scan => scan.State == SchemaScanState.Queued.ToString()).OrderBy(scan => scan.CreatedUtc).ThenBy(scan => scan.ScanId).FirstOrDefault();
        if (row is null) return Task.FromResult<SchemaScan?>(null);
        var affected = db.Execute("UPDATE SchemaScans SET State = @state, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @queued", new DataParameter[] { new("state", SchemaScanState.Running.ToString()), new("updatedUtc", Stamp(clock.UtcNow)), new("scanId", row.ScanId), new("queued", SchemaScanState.Queued.ToString()) });
        if (affected != 1) return Task.FromResult<SchemaScan?>(null);
        transaction.Commit();
        row.State = SchemaScanState.Running.ToString();
        return Task.FromResult<SchemaScan?>(ToScan(row));
    }

    internal Task CompleteAsync(SchemaScan scan, SchemaSnapshotContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var snapshot = new SchemaSnapshotRow { SnapshotId = Guid.NewGuid().ToString(), ConnectionId = scan.ConnectionId.ToString(), SnapshotHash = CanonicalSchemaSnapshotHasher.Hash(content), ContentJson = JsonSerializer.Serialize(ToRow(content)), CreatedUtc = Stamp(clock.UtcNow) };
        db.Execute("INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)", new DataParameter[] { new("snapshotId", snapshot.SnapshotId), new("connectionId", snapshot.ConnectionId), new("snapshotHash", snapshot.SnapshotHash), new("contentJson", snapshot.ContentJson), new("createdUtc", snapshot.CreatedUtc) });
        db.Execute("UPDATE SchemaScans SET State = @state, SnapshotId = @snapshotId, SnapshotHash = @snapshotHash, FailureCode = NULL, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @running", new DataParameter[] { new("state", SchemaScanState.Completed.ToString()), new("snapshotId", snapshot.SnapshotId), new("snapshotHash", snapshot.SnapshotHash), new("updatedUtc", Stamp(clock.UtcNow)), new("scanId", scan.ScanId.ToString()), new("running", SchemaScanState.Running.ToString()) });
        transaction.Commit();
        return Task.CompletedTask;
    }

    internal Task FailAsync(Guid scanId, string failureCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        db.Execute("UPDATE SchemaScans SET State = @state, FailureCode = @failureCode, UpdatedUtc = @updatedUtc WHERE ScanId = @scanId AND State = @running", new DataParameter[] { new("state", SchemaScanState.Failed.ToString()), new("failureCode", failureCode), new("updatedUtc", Stamp(clock.UtcNow)), new("scanId", scanId.ToString()), new("running", SchemaScanState.Running.ToString()) });
        return Task.CompletedTask;
    }

    private static SchemaScan ToScan(SchemaScanRow row) => new(Guid.Parse(row.ScanId), Guid.Parse(row.ConnectionId), Enum.Parse<SchemaScanState>(row.State), row.SnapshotId is null ? null : Guid.Parse(row.SnapshotId), row.SnapshotHash, row.FailureCode);
    private static DataParameter[] Parameters(SchemaScanRow row) => new DataParameter[] { new("scanId", row.ScanId), new("connectionId", row.ConnectionId), new("idempotencyKey", row.IdempotencyKey), new("state", row.State), new("createdUtc", row.CreatedUtc), new("updatedUtc", row.UpdatedUtc) };
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static bool Same(SchemaTableAddress address, string schema, string table) => string.Equals(address.Schema, schema, StringComparison.Ordinal) && string.Equals(address.Name, table, StringComparison.Ordinal);
    private static IEnumerable<SchemaTableAddress> Addresses(IEnumerable<SchemaTable> tables) => tables.Select(table => new SchemaTableAddress(table.Schema, table.Name)).OrderBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal);
    private static IEnumerable<SchemaGraphEdge> Edges(IEnumerable<SchemaForeignKey> foreignKeys) => foreignKeys.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new SchemaGraphEdge(item.ChildTable, item.ParentTable, item.Name));
    private static SnapshotRow ToRow(SchemaSnapshotContent content) => new(content.Tables.Select(table => new TableRow(table.Schema, table.Name, table.Columns.Select(column => new ColumnRow(column.Name, column.StoreType, column.ClrType, column.IsNullable)).ToArray(), table.PrimaryKey is null ? null : ToRow(table.PrimaryKey), table.UniqueConstraints.Select(ToRow).ToArray())).ToArray(), content.ForeignKeys.Select(foreignKey => new ForeignKeyRow(foreignKey.Name, ToRow(foreignKey.ChildTable), ToRow(foreignKey.ParentTable), foreignKey.ChildColumns.ToArray(), foreignKey.ParentColumns.ToArray(), foreignKey.IsEnforced, foreignKey.IsTrusted)).ToArray(), content.DatabaseIdentity, content.ProviderVersion);
    private static SchemaSnapshotContent FromRow(SnapshotRow row) => new(row.Tables.Select(table => new SchemaTable(table.Schema, table.Name, table.Columns.Select(column => new SchemaColumn(column.Name, column.StoreType, column.ClrType, column.IsNullable)), table.PrimaryKey is null ? null : FromRow(table.PrimaryKey), table.UniqueConstraints.Select(FromRow))), row.ForeignKeys.Select(foreignKey => new SchemaForeignKey(foreignKey.Name, FromRow(foreignKey.ChildTable), FromRow(foreignKey.ParentTable), foreignKey.ChildColumns, foreignKey.ParentColumns, foreignKey.IsEnforced, foreignKey.IsTrusted)), row.DatabaseIdentity, row.ProviderVersion);
    private static KeyRow ToRow(SchemaKey key) => new(key.Name, key.Columns.ToArray());
    private static SchemaKey FromRow(KeyRow row) => new(row.Name, row.Columns);
    private static AddressRow ToRow(SchemaTableAddress address) => new(address.Schema, address.Name);
    private static SchemaTableAddress FromRow(AddressRow row) => new(row.Schema, row.Name);

    private sealed class AddressComparer : IEqualityComparer<SchemaTableAddress>
    {
        public static AddressComparer Instance { get; } = new();
        public bool Equals(SchemaTableAddress? first, SchemaTableAddress? second) => first is null ? second is null : second is not null && string.Equals(first.Schema, second.Schema, StringComparison.Ordinal) && string.Equals(first.Name, second.Name, StringComparison.Ordinal);
        public int GetHashCode(SchemaTableAddress value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Schema), StringComparer.Ordinal.GetHashCode(value.Name));
    }

    private sealed record SnapshotRow(TableRow[] Tables, ForeignKeyRow[] ForeignKeys, string DatabaseIdentity, string ProviderVersion);
    private sealed record TableRow(string Schema, string Name, ColumnRow[] Columns, KeyRow? PrimaryKey, KeyRow[] UniqueConstraints);
    private sealed record ColumnRow(string Name, string StoreType, string ClrType, bool IsNullable);
    private sealed record KeyRow(string Name, string[] Columns);
    private sealed record ForeignKeyRow(string Name, AddressRow ChildTable, AddressRow ParentTable, string[] ChildColumns, string[] ParentColumns, bool IsEnforced, bool IsTrusted);
    private sealed record AddressRow(string Schema, string Name);
}
