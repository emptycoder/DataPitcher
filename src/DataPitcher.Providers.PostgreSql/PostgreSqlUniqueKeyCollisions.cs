using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

/// <summary>
/// Finds planned rows whose value on a unique key of the target belongs to a different target row. The planned
/// rows' key and unique-key values are read from the source through the sealed keys, copied into a temporary table
/// on the target, and joined against the target set-based; never one query per row.
/// </summary>
public static class PostgreSqlUniqueKeyCollisions
{
    private const int SampleCount = 3;

    public static async Task<IReadOnlyList<UniqueKeyCollision>> FindAsync(
        NpgsqlDataSource source,
        NpgsqlDataSource target,
        Guid planId,
        TableDefinition table,
        IReadOnlyList<string> stableKeys,
        CancellationToken cancellationToken
    )
    {
        var shape = await new PostgreSqlTransferSchemaReader(target).ReadAsync(
            table.Schema,
            table.Name,
            stableKeys,
            cancellationToken
        );
        var address = new TableAddress(table.Schema, table.Name);
        var result = new List<UniqueKeyCollision>();
        foreach (var unique in shape.UniqueKeys)
        {
            var columns = shape.StableKeyColumns.Concat(unique).DistinctBy(column => column.Name).ToArray();
            var candidates = await ReadPlannedAsync(source, planId, shape, columns, cancellationToken);
            var collision = await ProbeAsync(target, shape, unique, columns, candidates, cancellationToken);
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

    private static async Task<List<object?[]>> ReadPlannedAsync(
        NpgsqlDataSource source,
        Guid planId,
        PostgreSqlWriteTable shape,
        IReadOnlyList<PostgreSqlWriteColumn> columns,
        CancellationToken cancellationToken
    )
    {
        var sql =
            "SELECT "
            + string.Join(", ", columns.Select(column => "s." + PostgreSqlIdentifier.Quote(column.Name)))
            + " FROM "
            + PostgreSqlIdentifier.Qualified(shape.Target.Schema, shape.Target.Name)
            + " s JOIN "
            + PostgreSqlStagingTables.Qualified(PostgreSqlStagingTables.SourceTableName(planId, shape.Target))
            + " f ON f.__included AND "
            + string.Join(
                " AND ",
                shape.StableKeyColumns.Select(
                    (column, index) => "s." + PostgreSqlIdentifier.Quote(column.Name) + " = f.k" + index
                )
            );
        var rows = new List<object?[]>();
        await using var command = source.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[columns.Count];
            for (var index = 0; index < columns.Count; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<(long Rows, IReadOnlyList<string> Samples)> ProbeAsync(
        NpgsqlDataSource target,
        PostgreSqlWriteTable shape,
        IReadOnlyList<PostgreSqlWriteColumn> unique,
        IReadOnlyList<PostgreSqlWriteColumn> columns,
        List<object?[]> candidates,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await target.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string temp = "datapitcher_candidates";
        await using (
            var create = new NpgsqlCommand(
                "CREATE TEMP TABLE "
                    + temp
                    + " ("
                    + string.Join(
                        ", ",
                        columns.Select(column => PostgreSqlIdentifier.Quote(column.Name) + " " + column.StoreType)
                    )
                    + ") ON COMMIT DROP",
                connection,
                transaction
            )
        )
            await create.ExecuteNonQueryAsync(cancellationToken);
        await using (
            var importer = await connection.BeginBinaryImportAsync(
                "COPY "
                    + temp
                    + " ("
                    + string.Join(", ", columns.Select(column => PostgreSqlIdentifier.Quote(column.Name)))
                    + ") FROM STDIN (FORMAT BINARY)",
                cancellationToken
            )
        )
        {
            foreach (var row in candidates)
            {
                await importer.StartRowAsync(cancellationToken);
                for (var index = 0; index < columns.Count; index++)
                    if (row[index] is null)
                        await importer.WriteNullAsync(cancellationToken);
                    else
                        await importer.WriteAsync(row[index]!, columns[index].ProviderType, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
        var targetName = PostgreSqlIdentifier.Qualified(shape.Target.Schema, shape.Target.Name);
        // PostgreSQL treats NULLs as distinct in unique indexes, and so does this.
        var match = string.Join(
            " AND ",
            unique.Select(column =>
                "c." + PostgreSqlIdentifier.Quote(column.Name) + " = t." + PostgreSqlIdentifier.Quote(column.Name)
            )
        );
        var keys = shape.StableKeyColumns;
        var same = string.Join(
            " AND ",
            keys.Select(column =>
                "c." + PostgreSqlIdentifier.Quote(column.Name) + " = t." + PostgreSqlIdentifier.Quote(column.Name)
            )
        );
        var select =
            string.Join(", ", keys.Select(column => "c." + PostgreSqlIdentifier.Quote(column.Name)))
            + ", "
            + string.Join(", ", keys.Select(column => "t." + PostgreSqlIdentifier.Quote(column.Name)));
        var from = " FROM " + temp + " c JOIN " + targetName + " t ON " + match + " WHERE NOT (" + same + ")";
        await using var count = new NpgsqlCommand("SELECT count(*)" + from, connection, transaction);
        var rows = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        var samples = new List<string>();
        if (rows > 0)
        {
            await using var sample = new NpgsqlCommand(
                "SELECT " + select + from + " LIMIT " + SampleCount,
                connection,
                transaction
            );
            await using var reader = await sample.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                samples.Add(Describe(keys, reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return (rows, samples);
    }

    /// <summary>"id=5 (source) -> id=9 (target)" for the stable key columns read twice from the same row.</summary>
    internal static string Describe(IReadOnlyList<PostgreSqlWriteColumn> keys, System.Data.IDataRecord record) =>
        string.Join(", ", keys.Select((column, index) => column.Name + "=" + record.GetValue(index)))
        + " (source) -> "
        + string.Join(", ", keys.Select((column, index) => column.Name + "=" + record.GetValue(keys.Count + index)))
        + " (target)";
}
