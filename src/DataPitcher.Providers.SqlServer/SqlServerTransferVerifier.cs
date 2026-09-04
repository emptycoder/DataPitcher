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
/// Proves a completed run against its plan after the final batch committed: the run read every planned row
/// (manifest counts equal the sealed counts), every staged row is in the target, the keys it wrote or found equal
/// the manifest (StrictExact), and the rows it staged resolve every planned foreign key in the target. Bounded to
/// this run's rows; never a table scan of the target. Table and column names are resolved to the target's own
/// spelling before they are quoted.
/// </summary>
public sealed class SqlServerTransferVerifier(string targetConnectionString)
{
    public async Task VerifyAsync(
        TransferPlanContent content,
        SqlServerExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var planned = content.Tables.Where(table => table.Manifest.PlannedWrites > 0).ToArray();
        var reader = new SqlServerTransferSchemaReader(targetConnectionString);
        var shapes = new Dictionary<TableAddress, SqlServerWriteTable>();
        foreach (var table in planned)
            shapes[table.Mapping.Source] = await reader.ReadAsync(
                table.Mapping.Target.Schema,
                table.Mapping.Target.Name,
                KeyColumns(content, table),
                cancellationToken
            );
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await VerifyCountsAsync(connection, planned, shapes, context, cancellationToken);
        foreach (var table in planned)
            await VerifyPresenceAsync(connection, shapes[table.Mapping.Source], context, cancellationToken);
        if (content.VerificationStrategy == VerificationStrategy.StrictExact)
            await new SqlServerStrictExact(targetConnectionString).VerifyAsync(context, cancellationToken);
        foreach (var table in planned)
        foreach (
            var relationship in content.Relationships.Where(relationship =>
                Same(relationship.From, table.Mapping.Source)
            )
        )
        {
            var parentPlan = content.Tables.FirstOrDefault(candidate =>
                Same(candidate.Mapping.Source, relationship.To)
            );
            var parent = await reader.ReadAsync(
                parentPlan?.Mapping.Target.Schema ?? relationship.To.Schema,
                parentPlan?.Mapping.Target.Name ?? relationship.To.Name,
                relationship
                    .ToColumns.Select(column => parentPlan is null ? column : TargetColumn(parentPlan, column))
                    .ToArray(),
                cancellationToken
            );
            await VerifyRelationshipAsync(
                connection,
                shapes[table.Mapping.Source],
                parent,
                table,
                relationship,
                context,
                cancellationToken
            );
        }
    }

    /// <summary>The manifest must hold exactly as many keys per table as sealing counted; fewer means rows were never read.</summary>
    private static async Task VerifyCountsAsync(
        SqlConnection connection,
        IReadOnlyList<PlanTable> planned,
        IReadOnlyDictionary<TableAddress, SqlServerWriteTable> shapes,
        SqlServerExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var counts = new Dictionary<(string Schema, string Name), long>();
        await using (
            var command = new SqlCommand(
                "SELECT table_schema,table_name,COUNT(*) FROM [datapitcher].[transfer_write_manifest] WHERE job_id=@job AND run_id=@run GROUP BY table_schema,table_name",
                connection
            )
        )
        {
            command.Parameters.AddWithValue("@job", context.JobId);
            command.Parameters.AddWithValue("@run", context.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                counts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
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
    private static async Task VerifyPresenceAsync(
        SqlConnection connection,
        SqlServerWriteTable shape,
        SqlServerExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var sql =
            "SELECT COUNT(*) FROM "
            + SqlServerBatchStageWriter.StageName(shape)
            + " s WHERE s.job_id=@job AND s.run_id=@run AND NOT EXISTS (SELECT 1 FROM "
            + SqlServerIdentifier.Qualified(shape.Target.Schema, shape.Target.Name)
            + " t WHERE "
            + string.Join(
                " AND ",
                shape.StableKeyColumns.Select(column =>
                    "t." + SqlServerIdentifier.Quote(column.Name) + "=s." + SqlServerIdentifier.Quote(column.Name)
                )
            )
            + ")";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job", context.JobId);
        command.Parameters.AddWithValue("@run", context.RunId);
        var missing = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (missing != 0)
            throw new TransferVerificationException(
                $"{missing} planned row(s) of {shape.Target.Schema}.{shape.Target.Name} are not in the target."
            );
    }

    /// <summary>
    /// Every row this run staged must resolve its foreign key in the target now. Composite keys follow MATCH SIMPLE:
    /// a NULL in any column means there is nothing to resolve.
    /// </summary>
    private static async Task VerifyRelationshipAsync(
        SqlConnection connection,
        SqlServerWriteTable child,
        SqlServerWriteTable parent,
        PlanTable table,
        RelationshipPolicy relationship,
        SqlServerExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var childColumns = relationship
            .FromColumns.Select(column => child.Column(TargetColumn(table, column)).Name)
            .ToArray();
        var parentColumns = parent.StableKeyColumns.Select(column => column.Name).ToArray();
        var sql =
            "SELECT COUNT(*) FROM "
            + SqlServerBatchStageWriter.StageName(child)
            + " s JOIN "
            + SqlServerIdentifier.Qualified(child.Target.Schema, child.Target.Name)
            + " c ON "
            + string.Join(
                " AND ",
                child.StableKeyColumns.Select(column =>
                    "s." + SqlServerIdentifier.Quote(column.Name) + "=c." + SqlServerIdentifier.Quote(column.Name)
                )
            )
            + " WHERE s.job_id=@job AND s.run_id=@run AND "
            + string.Join(
                " AND ",
                childColumns.Select(column => "c." + SqlServerIdentifier.Quote(column) + " IS NOT NULL")
            )
            + " AND NOT EXISTS (SELECT 1 FROM "
            + SqlServerIdentifier.Qualified(parent.Target.Schema, parent.Target.Name)
            + " p WHERE "
            + string.Join(
                " AND ",
                childColumns
                    .Zip(parentColumns)
                    .Select(pair =>
                        "p." + SqlServerIdentifier.Quote(pair.Second) + "=c." + SqlServerIdentifier.Quote(pair.First)
                    )
            )
            + ")";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job", context.JobId);
        command.Parameters.AddWithValue("@run", context.RunId);
        var dangling = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (dangling != 0)
            throw new TransferVerificationException(
                $"{dangling} row(s) this run wrote to {table.Mapping.Target.Schema}.{table.Mapping.Target.Name} reference a {relationship.To.Schema}.{relationship.To.Name} row through {relationship.Name} that is not in the target."
            );
    }

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
