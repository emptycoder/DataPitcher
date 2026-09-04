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
/// Proves a completed run against its plan after the final batch committed: the run read every planned row
/// (manifest counts equal the sealed counts), every staged row is in the target, the keys it wrote or found equal
/// the manifest (StrictExact), and the rows it staged resolve every planned foreign key in the target. Bounded to
/// this run's rows; never a table scan of the target. Table and column names are resolved to the target's own
/// spelling before they are quoted.
/// </summary>
public sealed class PostgreSqlTransferVerifier(NpgsqlDataSource dataSource)
{
    public async Task VerifyAsync(
        TransferPlanContent content,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var planned = content.Tables.Where(table => table.Manifest.PlannedWrites > 0).ToArray();
        var reader = new PostgreSqlTransferSchemaReader(dataSource);
        var shapes = new Dictionary<TableAddress, PostgreSqlWriteTable>();
        foreach (var table in planned)
            shapes[table.Mapping.Source] = await reader.ReadAsync(
                table.Mapping.Target.Schema,
                table.Mapping.Target.Name,
                KeyColumns(content, table),
                cancellationToken
            );
        await VerifyCountsAsync(planned, shapes, context, cancellationToken);
        foreach (var table in planned)
            await VerifyPresenceAsync(shapes[table.Mapping.Source], context, cancellationToken);
        if (content.VerificationStrategy == VerificationStrategy.StrictExact)
            await new PostgreSqlStrictExact(dataSource).VerifyAsync(context, cancellationToken);
        foreach (var table in planned)
        foreach (
            var relationship in content.Relationships.Where(relationship =>
                Same(relationship.From, table.Mapping.Source)
            )
        )
        {
            var child = shapes[table.Mapping.Source];
            var childColumns = relationship
                .FromColumns.Select(column => child.Column(TargetColumn(table, column)).Name)
                .ToArray();
            if (!await ReferencesAsync(child, childColumns, context, cancellationToken))
                continue;
            var parentPlan = content.Tables.FirstOrDefault(candidate =>
                Same(candidate.Mapping.Source, relationship.To)
            );
            var parentKeys = relationship
                .ToColumns.Select(column => parentPlan is null ? column : TargetColumn(parentPlan, column))
                .ToArray();
            var parent = await reader.ReadAsync(
                parentPlan?.Mapping.Target.Schema ?? relationship.To.Schema,
                parentPlan?.Mapping.Target.Name ?? relationship.To.Name,
                parentKeys,
                cancellationToken
            );
            await VerifyRelationshipAsync(
                child,
                childColumns,
                parent,
                parentKeys.Select(column => parent.Column(column).Name).ToArray(),
                table,
                relationship,
                context,
                cancellationToken
            );
        }
    }

    /// <summary>The manifest must hold exactly as many keys per table as sealing counted; fewer means rows were never read.</summary>
    private async Task VerifyCountsAsync(
        IReadOnlyList<PlanTable> planned,
        IReadOnlyDictionary<TableAddress, PostgreSqlWriteTable> shapes,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var counts = new Dictionary<(string Schema, string Name), long>();
        await using (
            var command = dataSource.CreateCommand(
                "SELECT table_schema,table_name,count(*) FROM datapitcher.transfer_write_manifest WHERE job_id=@job AND run_id=@run GROUP BY table_schema,table_name"
            )
        )
        {
            command.Parameters.AddWithValue("job", context.JobId);
            command.Parameters.AddWithValue("run", context.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                counts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        }
        var mismatches = planned
            .Select(table =>
            {
                var target = shapes[table.Mapping.Source].Target;
                return (Table: table, Read: counts.GetValueOrDefault((target.Schema, target.Name)));
            })
            .Where(item => item.Read != item.Table.Manifest.PlannedWrites)
            .Select(item =>
                $"{item.Table.Mapping.Target.Schema}.{item.Table.Mapping.Target.Name}: the plan sealed {item.Table.Manifest.PlannedWrites} row(s) but the run moved {item.Read}"
            )
            .ToArray();
        if (mismatches.Length > 0)
            throw new TransferVerificationException(
                "The run did not move the planned row set: " + string.Join("; ", mismatches) + "."
            );
    }

    /// <summary>Every row this run staged must exist in the target now, whatever the ledger says about it.</summary>
    private async Task VerifyPresenceAsync(
        PostgreSqlWriteTable shape,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var sql =
            "SELECT count(*) FROM "
            + PostgreSqlBatchStageWriter.StageName(shape)
            + " s WHERE s.job_id=@job AND s.run_id=@run AND NOT EXISTS (SELECT 1 FROM "
            + PostgreSqlIdentifier.Qualified(shape.Target.Schema, shape.Target.Name)
            + " t WHERE "
            + string.Join(
                " AND ",
                shape.StableKeyColumns.Select(column =>
                    "t." + PostgreSqlIdentifier.Quote(column.Name) + " = s." + PostgreSqlIdentifier.Quote(column.Name)
                )
            )
            + ")";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        var missing = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (missing != 0)
            throw new TransferVerificationException(
                $"{missing} planned row(s) of {shape.Target.Schema}.{shape.Target.Name} are not in the target."
            );
    }

    /// <summary>
    /// Whether any row this run staged carries a value in every column of the foreign key. A NULL reference has no
    /// parent to resolve (MATCH SIMPLE), so a run whose references are all NULL never needs the parent table.
    /// </summary>
    private async Task<bool> ReferencesAsync(
        PostgreSqlWriteTable child,
        IReadOnlyList<string> childColumns,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        await using var command = dataSource.CreateCommand(ReferencingSql(child, childColumns));
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! != 0;
    }

    /// <summary>
    /// Every row this run staged must resolve its foreign key in the target now. Child and parent columns are paired
    /// by the foreign key's own definition, never by the parent's catalog order.
    /// </summary>
    private async Task VerifyRelationshipAsync(
        PostgreSqlWriteTable child,
        IReadOnlyList<string> childColumns,
        PostgreSqlWriteTable parent,
        IReadOnlyList<string> parentColumns,
        PlanTable table,
        RelationshipPolicy relationship,
        PostgreSqlExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var sql =
            ReferencingSql(child, childColumns)
            + " AND NOT EXISTS (SELECT 1 FROM "
            + PostgreSqlIdentifier.Qualified(parent.Target.Schema, parent.Target.Name)
            + " p WHERE "
            + string.Join(
                " AND ",
                childColumns
                    .Zip(parentColumns)
                    .Select(pair =>
                        "p."
                        + PostgreSqlIdentifier.Quote(pair.Second)
                        + " = c."
                        + PostgreSqlIdentifier.Quote(pair.First)
                    )
            )
            + ")";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("job", context.JobId);
        command.Parameters.AddWithValue("run", context.RunId);
        var dangling = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (dangling != 0)
            throw new TransferVerificationException(
                $"{dangling} row(s) this run wrote to {table.Mapping.Target.Schema}.{table.Mapping.Target.Name} reference a {relationship.To.Schema}.{relationship.To.Name} row through {relationship.Name} that is not in the target."
            );
    }

    /// <summary>Counts this run's staged rows whose foreign-key columns are all non-NULL; the table alias is c.</summary>
    private static string ReferencingSql(PostgreSqlWriteTable child, IReadOnlyList<string> childColumns) =>
        "SELECT COUNT(*) FROM "
        + PostgreSqlBatchStageWriter.StageName(child)
        + " s JOIN "
        + PostgreSqlIdentifier.Qualified(child.Target.Schema, child.Target.Name)
        + " c ON "
        + string.Join(
            " AND ",
            child.StableKeyColumns.Select(column =>
                "s." + PostgreSqlIdentifier.Quote(column.Name) + " = c." + PostgreSqlIdentifier.Quote(column.Name)
            )
        )
        + " WHERE s.job_id=@job AND s.run_id=@run AND "
        + string.Join(
            " AND ",
            childColumns.Select(column => "c." + PostgreSqlIdentifier.Quote(column) + " IS NOT NULL")
        );

    private static string[] KeyColumns(TransferPlanContent content, PlanTable table) =>
        content
            .StableKeys.Single(key => Same(key.Table, table.Mapping.Source))
            .Columns.Select(column => TargetColumn(table, column))
            .ToArray();

    private static string TargetColumn(PlanTable table, string sourceColumn) =>
        table.Mapping.Columns.Single(mapping => DatabaseNames.Equals(mapping.Source, sourceColumn)).Target;

    private static bool Same(TableAddress left, TableAddress right) =>
        DatabaseNames.Equals(left.Schema, right.Schema) && DatabaseNames.Equals(left.Name, right.Name);
}
