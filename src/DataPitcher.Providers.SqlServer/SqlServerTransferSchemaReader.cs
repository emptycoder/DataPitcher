using System.Data;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerTransferSchemaReader(string connectionString)
{
    public async Task<SqlServerWriteTable> ReadAsync(
        string schema,
        string table,
        IReadOnlyCollection<string> stableKeys,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            "SELECT c.name,ty.name,c.max_length,c.is_nullable,c.is_identity,c.is_computed,CASE WHEN ty.name='timestamp' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,c.collation_name,c.precision,c.scale FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<SqlServerWriteColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var typeName = reader.GetString(1);
            var computed = reader.GetBoolean(5);
            var rowVersion = reader.GetBoolean(6);
            var mapped = computed || rowVersion ? (typeof(byte[]), SqlDbType.Variant) : Map(typeName);
            columns.Add(
                new SqlServerWriteColumn(
                    name,
                    SqlServerCatalogReader.StoreType(
                        typeName,
                        reader.GetInt16(2),
                        reader.GetByte(8),
                        reader.GetByte(9)
                    ),
                    mapped.Item1,
                    mapped.Item2,
                    stableKeys.Contains(name, StringComparer.Ordinal),
                    reader.GetBoolean(4),
                    computed,
                    rowVersion,
                    reader.GetBoolean(3),
                    reader.IsDBNull(7) ? null : reader.GetString(7)
                )
            );
        }
        await reader.CloseAsync();
        return new SqlServerWriteTable(
            new TableAddress(schema, table),
            columns,
            await UniqueKeysAsync(connection, schema, table, cancellationToken)
        );
    }

    /// <summary>Column sets of every unfiltered unique constraint or index on the table, key columns in index order.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<string>>> UniqueKeysAsync(
        SqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken
    )
    {
        const string sql =
            "SELECT i.index_id,c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=0 JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID(@target) AND i.is_unique=1 AND i.has_filter=0 ORDER BY i.index_id,ic.key_ordinal";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@target", SqlServerIdentifier.Qualified(schema, table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new Dictionary<int, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!keys.TryGetValue(reader.GetInt32(0), out var columns))
                keys[reader.GetInt32(0)] = columns = [];
            columns.Add(reader.GetString(1));
        }
        return keys.OrderBy(pair => pair.Key).Select(pair => (IReadOnlyList<string>)pair.Value).ToArray();
    }

    /// <summary>CLR and provider types used to bulk-copy values; the declared store type still drives staging DDL.</summary>
    internal static (Type, SqlDbType) Map(string type) =>
        type switch
        {
            "bigint" => (typeof(long), SqlDbType.BigInt),
            "int" => (typeof(int), SqlDbType.Int),
            "smallint" => (typeof(short), SqlDbType.SmallInt),
            "tinyint" => (typeof(byte), SqlDbType.TinyInt),
            "bit" => (typeof(bool), SqlDbType.Bit),
            "decimal" or "numeric" => (typeof(decimal), SqlDbType.Decimal),
            "money" => (typeof(decimal), SqlDbType.Money),
            "smallmoney" => (typeof(decimal), SqlDbType.SmallMoney),
            "float" => (typeof(double), SqlDbType.Float),
            "real" => (typeof(float), SqlDbType.Real),
            "date" => (typeof(DateTime), SqlDbType.Date),
            "time" => (typeof(TimeSpan), SqlDbType.Time),
            "datetime" => (typeof(DateTime), SqlDbType.DateTime),
            "datetime2" => (typeof(DateTime), SqlDbType.DateTime2),
            "smalldatetime" => (typeof(DateTime), SqlDbType.SmallDateTime),
            "datetimeoffset" => (typeof(DateTimeOffset), SqlDbType.DateTimeOffset),
            "char" => (typeof(string), SqlDbType.Char),
            "varchar" => (typeof(string), SqlDbType.VarChar),
            "text" => (typeof(string), SqlDbType.Text),
            "nchar" => (typeof(string), SqlDbType.NChar),
            "nvarchar" or "sysname" => (typeof(string), SqlDbType.NVarChar),
            "ntext" => (typeof(string), SqlDbType.NText),
            "xml" => (typeof(string), SqlDbType.Xml),
            "binary" => (typeof(byte[]), SqlDbType.Binary),
            "varbinary" => (typeof(byte[]), SqlDbType.VarBinary),
            "image" => (typeof(byte[]), SqlDbType.Image),
            "uniqueidentifier" => (typeof(Guid), SqlDbType.UniqueIdentifier),
            "sql_variant" => (typeof(object), SqlDbType.Variant),
            _ => throw new NotSupportedException($"SQL Server transfer column type '{type}' is not supported."),
        };
}
