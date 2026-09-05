using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
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
    public PostgreSqlWriteTable(
        TableAddress target,
        IEnumerable<PostgreSqlWriteColumn> columns,
        IEnumerable<IReadOnlyList<string>>? uniqueKeys = null
    )
    {
        Target = target;
        Columns = Array.AsReadOnly(columns.ToArray());
        StableKeyColumns = Array.AsReadOnly(Columns.Where(x => x.IsStableKey).ToArray());
        var stable = StableKeyColumns.Select(column => column.Name).ToHashSet(DatabaseNames.Comparer);
        UniqueKeys = Array.AsReadOnly(
            (uniqueKeys ?? [])
                .Where(key => !stable.SetEquals(key))
                .Select(key => key.Select(Column).ToArray())
                .ToArray()
        );
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

    /// <summary>Other unique keys of the target (constraints and unique indexes); a row colliding on any with a different target row stops the run.</summary>
    public IReadOnlyList<IReadOnlyList<PostgreSqlWriteColumn>> UniqueKeys { get; }

    public PostgreSqlWriteColumn Column(string name) => Columns.Single(x => DatabaseNames.Equals(x.Name, name));
}

public sealed class PostgreSqlTransferRow
{
    public PostgreSqlTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values)
    {
        StableKey = stableKey;
        Values = new Dictionary<string, object?>(values, DatabaseNames.Comparer);
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
    TableAddress? LastTable = null,
    /// <summary>0 while rows are written, 1 once deferred columns are being filled in.</summary>
    int Phase = 0
);

public sealed record PostgreSqlResumePoint(long NextBatchSequence, StableKey? AfterStableKey);

public sealed record PostgreSqlBatchCommit(
    long Sequence,
    long Affected,
    long Inserts,
    long Updates,
    PostgreSqlTargetCheckpoint? Checkpoint = null
);

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

/// <summary>
/// Encodes a stable key for the checkpoint, ledger and manifest, where it is only ever compared for equality (joins,
/// EXCEPT, primary keys), never ordered. Each column's type is known on both sides, so a value needs only to be
/// unambiguous for its own type. The Integer, Bigint and Text layouts predate the other types and must not change:
/// paused jobs hold checkpoints in them. Temporal types carry a tag because Npgsql hands the same column back as
/// either of two CLR types (date as DateTime or DateOnly, timestamptz as DateTime or DateTimeOffset), and Decode
/// must return exactly what Encode received.
/// </summary>
public static class PostgreSqlStableKeyCodec
{
    private const byte Primary = 0;
    private const byte Alternate = 1;

    /// <summary>
    /// Types a stable key may use. Approximate numbers (real, double precision) compare unreliably; json has no
    /// equality operator and jsonb, xml, inet and macaddr keys are rare enough to wait for a request. Sealing refuses
    /// the rest up front.
    /// </summary>
    public static bool Supports(NpgsqlDbType type) =>
        type
            is NpgsqlDbType.Integer
                or NpgsqlDbType.Bigint
                or NpgsqlDbType.Smallint
                or NpgsqlDbType.Boolean
                or NpgsqlDbType.Numeric
                or NpgsqlDbType.Money
                or NpgsqlDbType.Text
                or NpgsqlDbType.Varchar
                or NpgsqlDbType.Char
                or NpgsqlDbType.Name
                or NpgsqlDbType.Uuid
                or NpgsqlDbType.Bytea
                or NpgsqlDbType.Date
                or NpgsqlDbType.Time
                or NpgsqlDbType.TimeTz
                or NpgsqlDbType.Timestamp
                or NpgsqlDbType.TimestampTz
                or NpgsqlDbType.Interval;

    public static byte[] Encode(StableKey key, PostgreSqlWriteTable table)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var column in table.StableKeyColumns)
        {
            var value = key.Components.Single(x => DatabaseNames.Equals(x.Column, column.Name)).Value;
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
        switch (type, value)
        {
            case (NpgsqlDbType.Integer, int integer):
                StableKeyBytes.WriteInt32(buffer, integer);
                return;
            case (NpgsqlDbType.Bigint, long bigInteger):
                StableKeyBytes.WriteInt64(buffer, bigInteger);
                return;
            case (NpgsqlDbType.Smallint, short small):
                StableKeyBytes.WriteInt16(buffer, small);
                return;
            case (NpgsqlDbType.Boolean, bool flag):
                StableKeyBytes.WriteByte(buffer, flag ? (byte)1 : (byte)0);
                return;
            case (NpgsqlDbType.Numeric or NpgsqlDbType.Money, decimal number):
                StableKeyBytes.WriteDecimal(buffer, number);
                return;
            case (NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Char or NpgsqlDbType.Name, string text):
                StableKeyBytes.WriteBytes(buffer, Encoding.UTF8.GetBytes(text));
                return;
            case (NpgsqlDbType.Uuid, Guid guid):
                StableKeyBytes.WriteGuid(buffer, guid);
                return;
            case (NpgsqlDbType.Bytea, byte[] bytes):
                StableKeyBytes.WriteBytes(buffer, bytes);
                return;
            case (NpgsqlDbType.Date, DateTime day):
                StableKeyBytes.WriteByte(buffer, Primary);
                StableKeyBytes.WriteDateTime(buffer, day);
                return;
            case (NpgsqlDbType.Date, DateOnly day):
                StableKeyBytes.WriteByte(buffer, Alternate);
                StableKeyBytes.WriteInt32(buffer, day.DayNumber);
                return;
            case (NpgsqlDbType.Time or NpgsqlDbType.Interval, TimeSpan span):
                StableKeyBytes.WriteByte(buffer, Primary);
                StableKeyBytes.WriteInt64(buffer, span.Ticks);
                return;
            case (NpgsqlDbType.Time, TimeOnly time):
                StableKeyBytes.WriteByte(buffer, Alternate);
                StableKeyBytes.WriteInt64(buffer, time.Ticks);
                return;
            case (NpgsqlDbType.Timestamp or NpgsqlDbType.TimestampTz, DateTime moment):
                StableKeyBytes.WriteByte(buffer, Primary);
                StableKeyBytes.WriteDateTime(buffer, moment);
                return;
            case (NpgsqlDbType.TimestampTz or NpgsqlDbType.TimeTz, DateTimeOffset moment):
                StableKeyBytes.WriteByte(buffer, Alternate);
                StableKeyBytes.WriteDateTimeOffset(buffer, moment);
                return;
            default:
                throw new NotSupportedException(
                    $"Stable-key type {type} is not supported for a value of type {value.GetType().Name}."
                );
        }
    }

    private static object Read(byte[] bytes, ref int offset, NpgsqlDbType type)
    {
        switch (type)
        {
            case NpgsqlDbType.Integer:
                return StableKeyBytes.ReadInt32(bytes, ref offset);
            case NpgsqlDbType.Bigint:
                return StableKeyBytes.ReadInt64(bytes, ref offset);
            case NpgsqlDbType.Smallint:
                return StableKeyBytes.ReadInt16(bytes, ref offset);
            case NpgsqlDbType.Boolean:
                return StableKeyBytes.ReadByte(bytes, ref offset) != 0;
            case NpgsqlDbType.Numeric or NpgsqlDbType.Money:
                return StableKeyBytes.ReadDecimal(bytes, ref offset);
            case NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Char or NpgsqlDbType.Name:
                return Encoding.UTF8.GetString(StableKeyBytes.ReadBytes(bytes, ref offset));
            case NpgsqlDbType.Uuid:
                return StableKeyBytes.ReadGuid(bytes, ref offset);
            case NpgsqlDbType.Bytea:
                return StableKeyBytes.ReadBytes(bytes, ref offset);
            case NpgsqlDbType.Date:
                return StableKeyBytes.ReadByte(bytes, ref offset) == Primary
                    ? StableKeyBytes.ReadDateTime(bytes, ref offset)
                    : DateOnly.FromDayNumber(StableKeyBytes.ReadInt32(bytes, ref offset));
            case NpgsqlDbType.Time:
                return StableKeyBytes.ReadByte(bytes, ref offset) == Primary
                    ? TimeSpan.FromTicks(StableKeyBytes.ReadInt64(bytes, ref offset))
                    : new TimeOnly(StableKeyBytes.ReadInt64(bytes, ref offset));
            case NpgsqlDbType.Interval:
                offset++;
                return TimeSpan.FromTicks(StableKeyBytes.ReadInt64(bytes, ref offset));
            case NpgsqlDbType.Timestamp or NpgsqlDbType.TimestampTz or NpgsqlDbType.TimeTz:
                return StableKeyBytes.ReadByte(bytes, ref offset) == Primary
                    ? (object)StableKeyBytes.ReadDateTime(bytes, ref offset)
                    : StableKeyBytes.ReadDateTimeOffset(bytes, ref offset);
            default:
                throw new NotSupportedException($"Stable-key type {type} is not supported.");
        }
    }
}
