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
/// (manifest counts equal the sealed counts), the keys it wrote or found equal the manifest (StrictExact), and the
/// rows it staged resolve every planned foreign key in the target. Bounded to this run's rows; never a table scan
/// of the target.
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
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(cancellationToken);
        await VerifyCountsAsync(connection, planned, context, cancellationToken);
        if (content.VerificationStrategy == VerificationStrategy.StrictExact)
            await new SqlServerStrictExact(targetConnectionString).VerifyAsync(context, cancellationToken);
        foreach (var table in planned)
        foreach (
            var relationship in content.Relationships.Where(relationship =>
                Same(relationship.From, table.Mapping.Source)
            )
        )
            await VerifyRelationshipAsync(connection, content, table, relationship, context, cancellationToken);
    }

    /// <summary>The manifest must hold exactly as many keys per table as sealing counted; fewer means rows were never read.</summary>
    private static async Task VerifyCountsAsync(
        SqlConnection connection,
        IReadOnlyList<PlanTable> planned,
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
                (Table: table, Read: counts.GetValueOrDefault((table.Mapping.Target.Schema, table.Mapping.Target.Name)))
            )
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

    /// <summary>
    /// Every row this run staged must resolve its foreign key in the target now. Composite keys follow MATCH SIMPLE:
    /// a NULL in any column means there is nothing to resolve.
    /// </summary>
    private static async Task VerifyRelationshipAsync(
        SqlConnection connection,
        TransferPlanContent content,
        PlanTable table,
        RelationshipPolicy relationship,
        SqlServerExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var parent = content.Tables.FirstOrDefault(candidate => Same(candidate.Mapping.Source, relationship.To));
        var childColumns = relationship.FromColumns.Select(column => TargetColumn(table, column)).ToArray();
        var parentColumns = relationship
            .ToColumns.Select(column => parent is null ? column : TargetColumn(parent, column))
            .ToArray();
        var keyColumns = content
            .StableKeys.Single(key => Same(key.Table, table.Mapping.Source))
            .Columns.Select(column => TargetColumn(table, column))
            .ToArray();
        var stage = SqlServerBatchStageWriter.StageName(table.Mapping.Target);
        var child = SqlServerIdentifier.Qualified(table.Mapping.Target.Schema, table.Mapping.Target.Name);
        var parentTable = SqlServerIdentifier.Qualified(
            parent?.Mapping.Target.Schema ?? relationship.To.Schema,
            parent?.Mapping.Target.Name ?? relationship.To.Name
        );
        var sql =
            "SELECT COUNT(*) FROM "
            + stage
            + " s JOIN "
            + child
            + " c ON "
            + string.Join(
                " AND ",
                keyColumns.Select(column =>
                    "s." + SqlServerIdentifier.Quote(column) + "=c." + SqlServerIdentifier.Quote(column)
                )
            )
            + " WHERE s.job_id=@job AND s.run_id=@run AND "
            + string.Join(
                " AND ",
                childColumns.Select(column => "c." + SqlServerIdentifier.Quote(column) + " IS NOT NULL")
            )
            + " AND NOT EXISTS (SELECT 1 FROM "
            + parentTable
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

    private static string TargetColumn(PlanTable table, string sourceColumn) =>
        table.Mapping.Columns.Single(mapping => StringComparer.Ordinal.Equals(mapping.Source, sourceColumn)).Target;

    private static bool Same(TableAddress left, TableAddress right) =>
        StringComparer.Ordinal.Equals(left.Schema, right.Schema)
        && StringComparer.Ordinal.Equals(left.Name, right.Name);
}
