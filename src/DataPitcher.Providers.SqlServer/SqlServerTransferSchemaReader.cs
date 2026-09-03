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
        return new SqlServerWriteTable(new TableAddress(schema, table), columns);
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
