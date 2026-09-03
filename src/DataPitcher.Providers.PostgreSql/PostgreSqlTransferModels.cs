using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public enum PostgreSqlConflictPolicy
{
    InsertOnly,
    SkipExisting,
    Upsert,
}

public sealed record PostgreSqlWriteColumn(
    string Name,
    string StoreType,
    NpgsqlDbType ProviderType,
    bool IsStableKey,
    bool IsGenerated,
    bool IsRowVersion,
    bool IsIdentityAlways,
    string? Collation
);

public sealed class PostgreSqlWriteTable
{
    public PostgreSqlWriteTable(TableAddress target, IEnumerable<PostgreSqlWriteColumn> columns)
    {
        Target = target;
        Columns = Array.AsReadOnly(columns.ToArray());
        StableKeyColumns = Array.AsReadOnly(Columns.Where(x => x.IsStableKey).ToArray());
        InsertColumns = Array.AsReadOnly(Columns.Where(x => !x.IsGenerated && !x.IsRowVersion).ToArray());
        UpdateColumns = Array.AsReadOnly(InsertColumns.Where(x => !x.IsStableKey && !x.IsIdentityAlways).ToArray());
        if (StableKeyColumns.Count == 0)
            throw new ArgumentException("A write table requires a stable key.");
    }

    public TableAddress Target { get; }
    public IReadOnlyList<PostgreSqlWriteColumn> Columns { get; }
    public IReadOnlyList<PostgreSqlWriteColumn> StableKeyColumns { get; }
    public IReadOnlyList<PostgreSqlWriteColumn> InsertColumns { get; }
    public IReadOnlyList<PostgreSqlWriteColumn> UpdateColumns { get; }

    public PostgreSqlWriteColumn Column(string name) =>
        Columns.Single(x => StringComparer.Ordinal.Equals(x.Name, name));
}

public sealed class PostgreSqlTransferRow
{
    public PostgreSqlTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values)
    {
        StableKey = stableKey;
        Values = new Dictionary<string, object?>(values, StringComparer.Ordinal);
    }

    public StableKey StableKey { get; }
    public IReadOnlyDictionary<string, object?> Values { get; }
}

public sealed class PostgreSqlTransferBatch
{
    public PostgreSqlTransferBatch(
        long sequence,
        IEnumerable<PostgreSqlTransferRow> rows,
        StableKey lastStableKey,
        PostgreSqlConflictPolicy policy
    )
    {
        Sequence = sequence;
        Rows = Array.AsReadOnly(rows.ToArray());
        LastStableKey = lastStableKey;
        Policy = policy;
    }

    public long Sequence { get; }
    public IReadOnlyList<PostgreSqlTransferRow> Rows { get; }
    public StableKey LastStableKey { get; }
    public PostgreSqlConflictPolicy Policy { get; }
}

public sealed record PostgreSqlExecutionContext(Guid JobId, Guid RunId, long FenceToken, string ManifestHash);

public sealed record PostgreSqlTargetCheckpoint(
    Guid JobId,
    Guid RunId,
    long LastBatchSequence,
    byte[] LastStableKey,
    long CumulativeAffected,
    long CumulativeInserts,
    long CumulativeUpdates,
    string ManifestHash,
    long FenceToken,
    TableAddress? LastTable = null
);

public sealed record PostgreSqlResumePoint(long NextBatchSequence, StableKey? AfterStableKey);

public sealed record PostgreSqlBatchCommit(long Sequence, long Affected, long Inserts, long Updates);

public interface IDerivedCheckpointMirror
{
    Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface IAfterTargetCommitBarrier
{
    Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed class PostgreSqlFenceLostException()
    : InvalidOperationException("The target checkpoint fence token no longer belongs to this worker.");

public sealed class PostgreSqlManifestMismatchException()
    : InvalidOperationException("The target checkpoint manifest hash differs from the sealed manifest.");

public sealed class PostgreSqlStrictExactBlockedException(string reason) : InvalidOperationException(reason);

public static class PostgreSqlStableKeyCodec
{
    public static byte[] Encode(StableKey key, PostgreSqlWriteTable table)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var column in table.StableKeyColumns)
        {
            var value = key.Components.Single(x => x.Column == column.Name).Value;
            if (value is null)
                throw new ArgumentException("Stable-key values cannot be null.");
            Write(buffer, value, column.ProviderType);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static StableKey Decode(byte[] bytes, PostgreSqlWriteTable table)
    {
        var offset = 0;
        var parts = new List<KeyComponent>();
        foreach (var column in table.StableKeyColumns)
            parts.Add(new(column.Name, Read(bytes, ref offset, column.ProviderType)));
        if (offset != bytes.Length)
            throw new ArgumentException("Stable-key encoding has trailing bytes.");
        return new StableKey(parts);
    }

    private static void Write(ArrayBufferWriter<byte> buffer, object value, NpgsqlDbType type)
    {
        var span = buffer.GetSpan(type is NpgsqlDbType.Integer ? 4 : 8);
        if (type == NpgsqlDbType.Integer && value is int integer)
        {
            BinaryPrimitives.WriteInt32BigEndian(span, integer);
            buffer.Advance(4);
            return;
        }
        if (type == NpgsqlDbType.Bigint && value is long bigInteger)
        {
            BinaryPrimitives.WriteInt64BigEndian(span, bigInteger);
            buffer.Advance(8);
            return;
        }
        var text =
            type == NpgsqlDbType.Text && value is string stringValue
                ? Encoding.UTF8.GetBytes(stringValue)
                : throw new NotSupportedException($"Stable-key type {type} is not supported.");
        BinaryPrimitives.WriteInt32BigEndian(span, text.Length);
        buffer.Advance(4);
        buffer.Write(text);
    }

    private static object Read(byte[] bytes, ref int offset, NpgsqlDbType type)
    {
        var span = bytes.AsSpan(offset);
        if (type == NpgsqlDbType.Integer)
        {
            offset += 4;
            return BinaryPrimitives.ReadInt32BigEndian(span);
        }
        if (type == NpgsqlDbType.Bigint)
        {
            offset += 8;
            return BinaryPrimitives.ReadInt64BigEndian(span);
        }
        if (type == NpgsqlDbType.Text)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(span);
            offset += 4;
            var value = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
            return value;
        }
        throw new NotSupportedException($"Stable-key type {type} is not supported.");
    }
}
