using System.Data;
using DataPitcher.Core.Plans;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerTransferSchemaReader(string connectionString)
{
    public async Task<SqlServerWriteTable> ReadAsync(string schema, string table, IReadOnlyCollection<string> stableKeys, CancellationToken cancellationToken)
    {
        const string sql = "SELECT c.name,ty.name,c.max_length,c.is_nullable,c.is_identity,c.is_computed,CASE WHEN ty.name='timestamp' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,c.collation_name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id";
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
            columns.Add(new SqlServerWriteColumn(name, StoreType(typeName, reader.GetInt16(2)), mapped.Item1, mapped.Item2, stableKeys.Contains(name, StringComparer.Ordinal), reader.GetBoolean(4), computed, rowVersion, reader.GetBoolean(3), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return new SqlServerWriteTable(new TableAddress(schema, table), columns);
    }

    private static (Type, SqlDbType) Map(string type) => type switch
    {
        "int" => (typeof(int), SqlDbType.Int),
        "bigint" => (typeof(long), SqlDbType.BigInt),
        "nvarchar" => (typeof(string), SqlDbType.NVarChar),
        _ => throw new NotSupportedException($"SQL Server transfer column type '{type}' is not supported.")
    };

    private static string StoreType(string type, short length) => type == "nvarchar" ? (length == -1 ? "nvarchar(max)" : $"nvarchar({length / 2})") : type;
}
