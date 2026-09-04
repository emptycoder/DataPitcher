using System.Data;
using System.Security.Cryptography;
using System.Text;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerBatchStageWriter
{
    private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);

    public async Task StageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SqlServerExecutionContext context,
        SqlServerWriteTable table,
        SqlServerTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        var stage = StageName(table);
        var columns = table.InsertColumns;
        var declarations = string.Join(
            ",",
            new[]
            {
                "[job_id] uniqueidentifier NOT NULL",
                "[run_id] uniqueidentifier NOT NULL",
                "[fence_token] bigint NOT NULL",
                "[batch_sequence] bigint NOT NULL",
            }.Concat(
                columns.Select(column =>
                    SqlServerIdentifier.Quote(column.Name)
                    + " "
                    + column.StoreType
                    // Text columns take the target column's collation so joins against the target never conflict.
                    + (column.Collation is null ? "" : " COLLATE " + column.Collation)
                    + (column.IsNullable ? " NULL" : " NOT NULL")
                )
            )
        );
        if (_ensured.Add(stage))
            await using (
                var create = new SqlCommand(
                    "IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'"
                        + stage.Replace("'", "''", StringComparison.Ordinal)
                        + "',N'U') IS NULL CREATE TABLE "
                        + stage
                        + " ("
                        + declarations
                        + ");",
                    connection,
                    transaction
                )
            )
                await create.ExecuteNonQueryAsync(cancellationToken);

        var data = new DataTable();
        data.Columns.Add("job_id", typeof(Guid));
        data.Columns.Add("run_id", typeof(Guid));
        data.Columns.Add("fence_token", typeof(long));
        data.Columns.Add("batch_sequence", typeof(long));
        foreach (var column in columns)
            data.Columns.Add(column.Name, column.ClrType);
        foreach (var row in batch.Rows)
        {
            var values = data.NewRow();
            values["job_id"] = context.JobId;
            values["run_id"] = context.RunId;
            values["fence_token"] = context.FenceToken;
            values["batch_sequence"] = batch.Sequence;
            foreach (var column in columns)
                values[column.Name] = row.Values[column.Name] ?? DBNull.Value;
            data.Rows.Add(values);
        }

        using var reader = data.CreateDataReader();
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction)
        {
            DestinationTableName = stage,
            BatchSize = 0,
            BulkCopyTimeout = 30,
            EnableStreaming = true,
        };
        for (var ordinal = 0; ordinal < data.Columns.Count; ordinal++)
            bulk.ColumnMappings.Add(ordinal, data.Columns[ordinal].ColumnName);
        await bulk.WriteToServerAsync(reader, cancellationToken);
    }

    public static string StageName(SqlServerWriteTable table) => StageName(table.Target);

    public static string StageName(TableAddress table) =>
        SqlServerIdentifier.Qualified(
            "datapitcher",
            "stage_"
                + Convert
                    .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table.Schema + "\u001f" + table.Name)))
                    .ToLowerInvariant()
        );
}
