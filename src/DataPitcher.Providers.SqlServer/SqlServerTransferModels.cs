using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Text;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Providers.SqlServer;

public enum SqlServerConflictPolicy
{
    InsertOnly,
    SkipExisting,
    Upsert,
}

public sealed record SqlServerWriteColumn(
    string Name,
    string StoreType,
    Type ClrType,
    SqlDbType ProviderType,
    bool IsStableKey,
    bool IsIdentity,
    bool IsComputed,
    bool IsRowVersion,
    bool IsNullable,
    string? Collation
);

public sealed class SqlServerWriteTable
{
    public SqlServerWriteTable(
        TableAddress target,
        IEnumerable<SqlServerWriteColumn> columns,
        IEnumerable<IReadOnlyList<string>>? uniqueKeys = null
    )
    {
        Target = target;
        Columns = Array.AsReadOnly(columns.ToArray());
        StableKeyColumns = Array.AsReadOnly(Columns.Where(column => column.IsStableKey).ToArray());
        var stable = StableKeyColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        UniqueKeys = Array.AsReadOnly(
            (uniqueKeys ?? [])
                .Where(key => !stable.SetEquals(key))
                .Select(key => key.Select(Column).ToArray())
                .ToArray()
        );
        InsertColumns = Array.AsReadOnly(Columns.Where(column => !column.IsComputed && !column.IsRowVersion).ToArray());
        UpdateColumns = Array.AsReadOnly(
            InsertColumns.Where(column => !column.IsStableKey && !column.IsIdentity).ToArray()
        );
        if (StableKeyColumns.Count == 0)
            throw new ArgumentException("A write table requires a stable key.");
    }

    public TableAddress Target { get; }
    public IReadOnlyList<SqlServerWriteColumn> Columns { get; }
    public IReadOnlyList<SqlServerWriteColumn> StableKeyColumns { get; }
    public IReadOnlyList<SqlServerWriteColumn> InsertColumns { get; }
    public IReadOnlyList<SqlServerWriteColumn> UpdateColumns { get; }

    /// <summary>Other unique keys of the target (constraints and unique indexes); a row colliding on any is skipped.</summary>
    public IReadOnlyList<IReadOnlyList<SqlServerWriteColumn>> UniqueKeys { get; }

    public SqlServerWriteColumn Column(string name) =>
        Columns.Single(column => StringComparer.Ordinal.Equals(column.Name, name));
}

public sealed class SqlServerTransferRow
{
    public SqlServerTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values)
    {
        StableKey = stableKey;
        Values = new Dictionary<string, object?>(values, StringComparer.Ordinal);
    }

    public StableKey StableKey { get; }
    public IReadOnlyDictionary<string, object?> Values { get; }
}

public sealed class SqlServerTransferBatch
{
    public SqlServerTransferBatch(
        long sequence,
        IEnumerable<SqlServerTransferRow> rows,
        StableKey lastStableKey,
        SqlServerConflictPolicy policy
    )
    {
        Sequence = sequence;
        Rows = Array.AsReadOnly(rows.ToArray());
        LastStableKey = lastStableKey;
        Policy = policy;
    }

    public long Sequence { get; }
    public IReadOnlyList<SqlServerTransferRow> Rows { get; }
    public StableKey LastStableKey { get; }
    public SqlServerConflictPolicy Policy { get; }
}

public sealed record SqlServerExecutionContext(Guid JobId, Guid RunId, long FenceToken, string ManifestHash);

public sealed record SqlServerTargetCheckpoint(
    Guid JobId,
    Guid RunId,
    long LastBatchSequence,
    byte[] LastStableKey,
    long CumulativeAffected,
    long CumulativeInserts,
    long CumulativeUpdates,
    string ManifestHash,
    long FenceToken,
    TableAddress? LastTable = null,
    /// <summary>0 while rows are written, 1 once deferred columns are being filled in.</summary>
    int Phase = 0
);

public sealed record SqlServerResumePoint(long NextBatchSequence, StableKey? AfterStableKey);

public sealed record SqlServerBatchCommit(
    long Sequence,
    long Affected,
    long Inserts,
    long Updates,
    SqlServerTargetCheckpoint? Checkpoint = null
);

public interface ISqlServerDerivedCheckpointMirror
{
    Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface ISqlServerAfterTargetCommitBarrier
{
    Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed class SqlServerFenceLostException()
    : InvalidOperationException("The target checkpoint fence token no longer belongs to this worker.");

public sealed class SqlServerManifestMismatchException()
    : InvalidOperationException("The target checkpoint manifest hash differs from the sealed manifest.");

public sealed class SqlServerStrictExactBlockedException(string reason) : InvalidOperationException(reason);

public static class SqlServerStableKeyCodec
{
    public static byte[] Encode(StableKey key, SqlServerWriteTable table)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var column in table.StableKeyColumns)
        {
            var value =
                key.Components.Single(component => component.Column == column.Name).Value
                ?? throw new ArgumentException("Stable-key values cannot be null.");
            Write(buffer, value, column.ProviderType);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static StableKey Decode(byte[] bytes, SqlServerWriteTable table)
    {
        var offset = 0;
        var components = new List<KeyComponent>();
        foreach (var column in table.StableKeyColumns)
            components.Add(new KeyComponent(column.Name, Read(bytes, ref offset, column.ProviderType)));
        if (offset != bytes.Length)
            throw new ArgumentException("Stable-key encoding has trailing bytes.");
        return new StableKey(components);
    }

    private static void Write(ArrayBufferWriter<byte> buffer, object value, SqlDbType type)
    {
        if (type == SqlDbType.Int && value is int integer)
        {
            var span = buffer.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(span, integer);
            buffer.Advance(4);
            return;
        }
        if (type == SqlDbType.BigInt && value is long bigint)
        {
            var span = buffer.GetSpan(8);
            BinaryPrimitives.WriteInt64BigEndian(span, bigint);
            buffer.Advance(8);
            return;
        }
        var text =
            type == SqlDbType.NVarChar && value is string stringValue
                ? Encoding.UTF8.GetBytes(stringValue)
                : throw new NotSupportedException($"Stable-key type {type} is not supported.");
        var length = buffer.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(length, text.Length);
        buffer.Advance(4);
        buffer.Write(text);
    }

    private static object Read(byte[] bytes, ref int offset, SqlDbType type)
    {
        if (type == SqlDbType.Int)
        {
            var result = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            return result;
        }
        if (type == SqlDbType.BigInt)
        {
            var result = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, 8));
            offset += 8;
            return result;
        }
        if (type == SqlDbType.NVarChar)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            var result = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
            return result;
        }
        throw new NotSupportedException($"Stable-key type {type} is not supported.");
    }
}
