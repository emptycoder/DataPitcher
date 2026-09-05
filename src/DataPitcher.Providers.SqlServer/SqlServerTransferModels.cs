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
        var stable = StableKeyColumns.Select(column => column.Name).ToHashSet(DatabaseNames.Comparer);
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

    /// <summary>Other unique keys of the target (constraints and unique indexes); a row colliding on any with a different target row stops the run.</summary>
    public IReadOnlyList<IReadOnlyList<SqlServerWriteColumn>> UniqueKeys { get; }

    public SqlServerWriteColumn Column(string name) =>
        Columns.Single(column => DatabaseNames.Equals(column.Name, name));
}

public sealed class SqlServerTransferRow
{
    public SqlServerTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values)
    {
        StableKey = stableKey;
        Values = new Dictionary<string, object?>(values, DatabaseNames.Comparer);
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

/// <summary>
/// Encodes a stable key for the checkpoint, ledger and manifest, where it is only ever compared for equality (joins,
/// EXCEPT, primary keys), never ordered. Each column's type is known on both sides, so a value needs only to be
/// unambiguous for its own type. The Int, BigInt and NVarChar layouts predate the other types and must not change:
/// paused jobs hold checkpoints in them.
/// </summary>
public static class SqlServerStableKeyCodec
{
    /// <summary>
    /// Types a stable key may use. Approximate numbers (float, real) compare unreliably; text, ntext, image and xml
    /// cannot be index keys on SQL Server; sql_variant has no single CLR type. Sealing refuses the rest up front.
    /// </summary>
    public static bool Supports(SqlDbType type) =>
        type
            is SqlDbType.Int
                or SqlDbType.BigInt
                or SqlDbType.SmallInt
                or SqlDbType.TinyInt
                or SqlDbType.Bit
                or SqlDbType.Char
                or SqlDbType.VarChar
                or SqlDbType.NChar
                or SqlDbType.NVarChar
                or SqlDbType.UniqueIdentifier
                or SqlDbType.Decimal
                or SqlDbType.Money
                or SqlDbType.SmallMoney
                or SqlDbType.Date
                or SqlDbType.DateTime
                or SqlDbType.DateTime2
                or SqlDbType.SmallDateTime
                or SqlDbType.DateTimeOffset
                or SqlDbType.Time
                or SqlDbType.Binary
                or SqlDbType.VarBinary;

    public static byte[] Encode(StableKey key, SqlServerWriteTable table)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var column in table.StableKeyColumns)
        {
            var value =
                key.Components.Single(component => DatabaseNames.Equals(component.Column, column.Name)).Value
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
        switch (type, value)
        {
            case (SqlDbType.Int, int integer):
                StableKeyBytes.WriteInt32(buffer, integer);
                return;
            case (SqlDbType.BigInt, long bigint):
                StableKeyBytes.WriteInt64(buffer, bigint);
                return;
            case (SqlDbType.SmallInt, short small):
                StableKeyBytes.WriteInt16(buffer, small);
                return;
            case (SqlDbType.TinyInt, byte tiny):
                StableKeyBytes.WriteByte(buffer, tiny);
                return;
            case (SqlDbType.Bit, bool bit):
                StableKeyBytes.WriteByte(buffer, bit ? (byte)1 : (byte)0);
                return;
            case (SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar, string text):
                StableKeyBytes.WriteBytes(buffer, Encoding.UTF8.GetBytes(text));
                return;
            case (SqlDbType.UniqueIdentifier, Guid guid):
                StableKeyBytes.WriteGuid(buffer, guid);
                return;
            case (SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney, decimal number):
                StableKeyBytes.WriteDecimal(buffer, number);
                return;
            case (
                SqlDbType.Date
                    or SqlDbType.DateTime
                    or SqlDbType.DateTime2
                    or SqlDbType.SmallDateTime,
                DateTime moment
            ):
                StableKeyBytes.WriteDateTime(buffer, moment);
                return;
            case (SqlDbType.DateTimeOffset, DateTimeOffset moment):
                StableKeyBytes.WriteDateTimeOffset(buffer, moment);
                return;
            case (SqlDbType.Time, TimeSpan time):
                StableKeyBytes.WriteInt64(buffer, time.Ticks);
                return;
            case (SqlDbType.Binary or SqlDbType.VarBinary, byte[] bytes):
                StableKeyBytes.WriteBytes(buffer, bytes);
                return;
            default:
                throw new NotSupportedException(
                    $"Stable-key type {type} is not supported for a value of type {value.GetType().Name}."
                );
        }
    }

    private static object Read(byte[] bytes, ref int offset, SqlDbType type) =>
        type switch
        {
            SqlDbType.Int => StableKeyBytes.ReadInt32(bytes, ref offset),
            SqlDbType.BigInt => StableKeyBytes.ReadInt64(bytes, ref offset),
            SqlDbType.SmallInt => StableKeyBytes.ReadInt16(bytes, ref offset),
            SqlDbType.TinyInt => StableKeyBytes.ReadByte(bytes, ref offset),
            SqlDbType.Bit => StableKeyBytes.ReadByte(bytes, ref offset) != 0,
            SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar => Encoding.UTF8.GetString(
                StableKeyBytes.ReadBytes(bytes, ref offset)
            ),
            SqlDbType.UniqueIdentifier => StableKeyBytes.ReadGuid(bytes, ref offset),
            SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney => StableKeyBytes.ReadDecimal(
                bytes,
                ref offset
            ),
            SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime =>
                StableKeyBytes.ReadDateTime(bytes, ref offset),
            SqlDbType.DateTimeOffset => StableKeyBytes.ReadDateTimeOffset(bytes, ref offset),
            SqlDbType.Time => TimeSpan.FromTicks(StableKeyBytes.ReadInt64(bytes, ref offset)),
            SqlDbType.Binary or SqlDbType.VarBinary => StableKeyBytes.ReadBytes(bytes, ref offset),
            _ => throw new NotSupportedException($"Stable-key type {type} is not supported."),
        };
}
