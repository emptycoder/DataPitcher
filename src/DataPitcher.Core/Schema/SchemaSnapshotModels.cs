using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DataPitcher.Core.Schema;

public sealed record SchemaTableAddress(string Schema, string Name);
public sealed record SchemaColumn(string Name, string StoreType, string ClrType, bool IsNullable);

public sealed record SchemaKey
{
    public SchemaKey(string name, IEnumerable<string> columns)
    {
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
}

public sealed class SchemaTable
{
    public SchemaTable(string schema, string name, IEnumerable<SchemaColumn> columns, SchemaKey? primaryKey,
        IEnumerable<SchemaKey> uniqueConstraints)
    {
        Schema = schema;
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
        PrimaryKey = primaryKey;
        UniqueConstraints = Array.AsReadOnly(uniqueConstraints.ToArray());
    }

    public string Schema { get; }
    public string Name { get; }
    public IReadOnlyList<SchemaColumn> Columns { get; }
    public SchemaKey? PrimaryKey { get; }
    public IReadOnlyList<SchemaKey> UniqueConstraints { get; }
}

public sealed class SchemaForeignKey
{
    public SchemaForeignKey(string name, SchemaTableAddress childTable, SchemaTableAddress parentTable,
        IEnumerable<string> childColumns, IEnumerable<string> parentColumns, bool isEnforced, bool isTrusted)
    {
        Name = name;
        ChildTable = childTable;
        ParentTable = parentTable;
        ChildColumns = Array.AsReadOnly(childColumns.ToArray());
        ParentColumns = Array.AsReadOnly(parentColumns.ToArray());
        IsEnforced = isEnforced;
        IsTrusted = isTrusted;
    }

    public string Name { get; }
    public SchemaTableAddress ChildTable { get; }
    public SchemaTableAddress ParentTable { get; }
    public IReadOnlyList<string> ChildColumns { get; }
    public IReadOnlyList<string> ParentColumns { get; }
    public bool IsEnforced { get; }
    public bool IsTrusted { get; }
}

public sealed class SchemaSnapshotContent
{
    public SchemaSnapshotContent(IEnumerable<SchemaTable> tables, IEnumerable<SchemaForeignKey> foreignKeys,
        string databaseIdentity = "", string providerVersion = "")
    {
        Tables = Array.AsReadOnly(tables.ToArray());
        ForeignKeys = Array.AsReadOnly(foreignKeys.ToArray());
        DatabaseIdentity = databaseIdentity;
        ProviderVersion = providerVersion;
    }

    public IReadOnlyList<SchemaTable> Tables { get; }
    public IReadOnlyList<SchemaForeignKey> ForeignKeys { get; }
    public string DatabaseIdentity { get; }
    public string ProviderVersion { get; }
}

public sealed record StoredSchemaSnapshot(
    Guid SnapshotId, Guid ConnectionId, string Hash, DateTimeOffset CapturedAtUtc, SchemaSnapshotContent Content);

public sealed record SchemaGraphEdge(SchemaTableAddress Child, SchemaTableAddress Parent, string ForeignKeyName);

public sealed class SchemaGraphProjection
{
    public SchemaGraphProjection(IEnumerable<SchemaTableAddress> tables, IEnumerable<SchemaGraphEdge> edges)
    {
        Tables = Array.AsReadOnly(tables.ToArray());
        Edges = Array.AsReadOnly(edges.ToArray());
    }

    public IReadOnlyList<SchemaTableAddress> Tables { get; }
    public IReadOnlyList<SchemaGraphEdge> Edges { get; }
}

public sealed class SchemaTableProjection
{
    public SchemaTableProjection(SchemaTable table, IEnumerable<SchemaForeignKey> foreignKeys)
    {
        Table = table;
        ForeignKeys = Array.AsReadOnly(foreignKeys.ToArray());
    }

    public SchemaTable Table { get; }
    public IReadOnlyList<SchemaForeignKey> ForeignKeys { get; }
}

public sealed class SchemaNeighbourhoodProjection
{
    public SchemaNeighbourhoodProjection(SchemaTableAddress center, int depth, IEnumerable<SchemaTableAddress> tables,
        IEnumerable<SchemaGraphEdge> edges)
    {
        Center = center;
        Depth = depth;
        Tables = Array.AsReadOnly(tables.ToArray());
        Edges = Array.AsReadOnly(edges.ToArray());
    }

    public SchemaTableAddress Center { get; }
    public int Depth { get; }
    public IReadOnlyList<SchemaTableAddress> Tables { get; }
    public IReadOnlyList<SchemaGraphEdge> Edges { get; }
}

public static class CanonicalSchemaSnapshotHasher
{
    public static string Hash(SchemaSnapshotContent snapshot)
    {
        var writer = new Writer();
        writer.Text("DataPitcher.SchemaSnapshot.v1");
        Unordered(writer, snapshot.Tables, Table);
        Unordered(writer, snapshot.ForeignKeys, ForeignKey);
        return Convert.ToHexString(SHA256.HashData(writer.Bytes));
    }

    private static void Table(Writer writer, SchemaTable table)
    {
        writer.Text(table.Schema);
        writer.Text(table.Name);
        Ordered(writer, table.Columns, Column);
        writer.Bool(table.PrimaryKey is not null);
        if (table.PrimaryKey is not null)
            Key(writer, table.PrimaryKey);
        Unordered(writer, table.UniqueConstraints, Key);
    }

    private static void Column(Writer writer, SchemaColumn column)
    {
        writer.Text(column.Name);
        writer.Text(column.StoreType);
        writer.Text(column.ClrType);
        writer.Bool(column.IsNullable);
    }

    private static void Key(Writer writer, SchemaKey key)
    {
        writer.Text(key.Name);
        Ordered(writer, key.Columns, static (nested, column) => nested.Text(column));
    }

    private static void ForeignKey(Writer writer, SchemaForeignKey foreignKey)
    {
        writer.Text(foreignKey.Name);
        Address(writer, foreignKey.ChildTable);
        Address(writer, foreignKey.ParentTable);
        Ordered(writer, foreignKey.ChildColumns, static (nested, column) => nested.Text(column));
        Ordered(writer, foreignKey.ParentColumns, static (nested, column) => nested.Text(column));
        writer.Bool(foreignKey.IsEnforced);
        writer.Bool(foreignKey.IsTrusted);
    }

    private static void Address(Writer writer, SchemaTableAddress address)
    {
        writer.Text(address.Schema);
        writer.Text(address.Name);
    }

    private static void Ordered<T>(Writer writer, IEnumerable<T> values, Action<Writer, T> item)
    {
        var all = values.ToArray();
        writer.Int(all.Length);
        foreach (var value in all)
            item(writer, value);
    }

    private static void Unordered<T>(Writer writer, IEnumerable<T> values, Action<Writer, T> item)
    {
        var all = values.Select(value =>
        {
            var nested = new Writer();
            item(nested, value);
            return nested.Bytes.ToArray();
        }).OrderBy(bytes => Convert.ToHexString(bytes), StringComparer.Ordinal).ToArray();
        writer.Int(all.Length);
        foreach (var value in all)
            writer.Raw(value);
    }

    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public ReadOnlySpan<byte> Bytes => _buffer.WrittenSpan;

        public void Bool(bool value) => Int(value ? 1 : 0);

        public void Int(int value)
        {
            var span = _buffer.GetSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(span, value);
            _buffer.Advance(4);
        }

        public void Text(string value)
        {
            Int(value.Length);
            foreach (var character in value)
            {
                var span = _buffer.GetSpan(2);
                BinaryPrimitives.WriteUInt16BigEndian(span, character);
                _buffer.Advance(2);
            }
        }

        public void Raw(ReadOnlySpan<byte> value)
        {
            var span = _buffer.GetSpan(value.Length);
            value.CopyTo(span);
            _buffer.Advance(value.Length);
        }
    }
}
