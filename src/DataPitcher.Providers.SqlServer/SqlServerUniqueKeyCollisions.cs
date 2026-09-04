using System.Data;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

/// <summary>
/// Finds planned rows whose value on a unique key of the target belongs to a different target row. The planned
/// rows' key and unique-key values are read from the source through the sealed keys, staged in a temporary table
/// on the target, and joined against the target set-based; never one query per row.
/// </summary>
public static class SqlServerUniqueKeyCollisions
{
    private const int SampleCount = 3;

    public static async Task<IReadOnlyList<UniqueKeyCollision>> FindAsync(
        string sourceConnectionString,
        string targetConnectionString,
        Guid planId,
        TableMapping mapping,
        IReadOnlyList<string> stableKeys,
        CancellationToken cancellationToken
    )
    {
        var shape = await new SqlServerTransferSchemaReader(targetConnectionString).ReadAsync(
            mapping.Target.Schema,
            mapping.Target.Name,
            stableKeys.Select(column => Target(mapping, column)).ToArray(),
            cancellationToken
        );
        var mapped = mapping.Columns.Select(column => column.Target).ToHashSet(DatabaseNames.Comparer);
        var address = mapping.Source;
        var result = new List<UniqueKeyCollision>();
        // A unique key over a column the plan does not write cannot collide on the rows' account.
        foreach (var unique in shape.UniqueKeys.Where(key => key.All(column => mapped.Contains(column.Name))))
        {
            var columns = shape.StableKeyColumns.Concat(unique).DistinctBy(column => column.Name).ToArray();
            var candidates = await ReadPlannedAsync(
                sourceConnectionString,
                planId,
                mapping,
                shape,
                columns,
                cancellationToken
            );
            var collision = await ProbeAsync(
                targetConnectionString,
                shape,
                unique,
                columns,
                candidates,
                cancellationToken
            );
            if (collision.Rows > 0)
                result.Add(
                    new UniqueKeyCollision(
                        address,
                        unique.Select(column => column.Name).ToArray(),
                        collision.Rows,
                        collision.Samples
                    )
                );
        }
        return result;
    }

    /// <summary>The planned rows' values for these target columns, read from the source columns the plan maps to them.</summary>
    private static async Task<DataTable> ReadPlannedAsync(
        string sourceConnectionString,
        Guid planId,
        TableMapping mapping,
        SqlServerWriteTable shape,
        IReadOnlyList<SqlServerWriteColumn> columns,
        CancellationToken cancellationToken
    )
    {
        var data = new DataTable();
        foreach (var column in columns)
            data.Columns.Add(column.Name, column.ClrType);
        var sql =
            "SELECT "
            + string.Join(",", columns.Select(column => "s." + SqlServerIdentifier.Quote(Source(mapping, column.Name))))
            + " FROM "
            + SqlServerIdentifier.Qualified(mapping.Source.Schema, mapping.Source.Name)
            + " s JOIN "
            + SqlServerStagingTables.Qualified(SqlServerStagingTables.SourceTableName(planId, mapping.Source))
            + " f ON f.[__included]=1 AND "
            + string.Join(
                " AND ",
                shape.StableKeyColumns.Select(
                    (column, index) =>
                        "s." + SqlServerIdentifier.Quote(Source(mapping, column.Name)) + "=f.[k" + index + "]"
                )
            );
        await using var connection = new SqlConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = data.NewRow();
            for (var index = 0; index < columns.Count; index++)
                row[index] = reader.GetValue(index);
            data.Rows.Add(row);
        }
        return data;
    }

    private static async Task<(long Rows, IReadOnlyList<string> Samples)> ProbeAsync(
        string targetConnectionString,
        SqlServerWriteTable shape,
        IReadOnlyList<SqlServerWriteColumn> unique,
        IReadOnlyList<SqlServerWriteColumn> columns,
        DataTable candidates,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (
            var create = new SqlCommand(
                "CREATE TABLE #candidates ("
                    + string.Join(
                        ",",
                        columns.Select(column =>
                            SqlServerIdentifier.Quote(column.Name)
                            + " "
                            + column.StoreType
                            + (column.Collation is null ? "" : " COLLATE " + column.Collation)
                            + " NULL"
                        )
                    )
                    + ")",
                connection
            )
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        using (var reader = candidates.CreateDataReader())
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#candidates", BulkCopyTimeout = 0 })
        {
            foreach (DataColumn column in candidates.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(reader, cancellationToken);
        }
        var target = SqlServerIdentifier.Qualified(shape.Target.Schema, shape.Target.Name);
        // SQL Server unique indexes treat NULL as one value, so NULLs compare equal here as well.
        var match = string.Join(
            " AND ",
            unique.Select(column =>
                "(c."
                + SqlServerIdentifier.Quote(column.Name)
                + "=t."
                + SqlServerIdentifier.Quote(column.Name)
                + " OR (c."
                + SqlServerIdentifier.Quote(column.Name)
                + " IS NULL AND t."
                + SqlServerIdentifier.Quote(column.Name)
                + " IS NULL))"
            )
        );
        var same = string.Join(
            " AND ",
            shape.StableKeyColumns.Select(column =>
                "c." + SqlServerIdentifier.Quote(column.Name) + "=t." + SqlServerIdentifier.Quote(column.Name)
            )
        );
        var keys = shape.StableKeyColumns;
        var select =
            string.Join(",", keys.Select(column => "c." + SqlServerIdentifier.Quote(column.Name)))
            + ","
            + string.Join(",", keys.Select(column => "t." + SqlServerIdentifier.Quote(column.Name)));
        var from = " FROM #candidates c JOIN " + target + " t ON " + match + " WHERE NOT (" + same + ")";
        await using var count = new SqlCommand("SELECT COUNT_BIG(*)" + from, connection);
        var rows = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        var samples = new List<string>();
        if (rows > 0)
        {
            await using var sample = new SqlCommand($"SELECT TOP ({SampleCount}) " + select + from, connection);
            await using var reader = await sample.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                samples.Add(Describe(keys, reader));
        }
        await using var drop = new SqlCommand("DROP TABLE #candidates", connection);
        await drop.ExecuteNonQueryAsync(cancellationToken);
        return (rows, samples);
    }

    private static string Target(TableMapping mapping, string source) =>
        mapping.Columns.Single(column => DatabaseNames.Equals(column.Source, source)).Target;

    private static string Source(TableMapping mapping, string target) =>
        mapping.Columns.Single(column => DatabaseNames.Equals(column.Target, target)).Source;

    /// <summary>"Id=5 (source) -> Id=9 (target)" for the stable key columns read twice from the same row.</summary>
    internal static string Describe(IReadOnlyList<SqlServerWriteColumn> keys, IDataRecord record) =>
        string.Join(", ", keys.Select((column, index) => column.Name + "=" + record.GetValue(index)))
        + " (source) -> "
        + string.Join(", ", keys.Select((column, index) => column.Name + "=" + record.GetValue(keys.Count + index)))
        + " (target)";
}
