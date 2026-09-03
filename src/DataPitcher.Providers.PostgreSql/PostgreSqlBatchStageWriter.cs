using System.Security.Cryptography;
using System.Text;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlBatchStageWriter
{
    private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);

    public async Task StageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlExecutionContext context,
        PostgreSqlWriteTable table,
        PostgreSqlTransferBatch batch,
        CancellationToken cancellationToken
    )
    {
        var stage = StageName(table);
        var columns = table.InsertColumns;
        var names = string.Join(
            ", ",
            new[] { "job_id", "run_id", "fence_token", "batch_sequence" }.Concat(
                columns.Select(x => PostgreSqlIdentifier.Quote(x.Name))
            )
        );
        var declaration = string.Join(
            ", ",
            new[]
            {
                "job_id uuid NOT NULL",
                "run_id uuid NOT NULL",
                "fence_token bigint NOT NULL",
                "batch_sequence bigint NOT NULL",
            }.Concat(columns.Select(x => PostgreSqlIdentifier.Quote(x.Name) + " " + x.StoreType))
        );
        if (_ensured.Add(stage))
            await ExecuteAsync(
                connection,
                transaction,
                "CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS "
                    + stage
                    + " ("
                    + declaration
                    + ")",
                cancellationToken
            );
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY " + stage + " (" + names + ") FROM STDIN (FORMAT BINARY)",
            cancellationToken
        );
        foreach (var row in batch.Rows)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(context.JobId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(context.RunId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(context.FenceToken, NpgsqlDbType.Bigint, cancellationToken);
            await importer.WriteAsync(batch.Sequence, NpgsqlDbType.Bigint, cancellationToken);
            foreach (var column in columns)
            {
                var value = row.Values[column.Name];
                if (value is null)
                    await importer.WriteNullAsync(cancellationToken);
                else
                    await importer.WriteAsync(value, column.ProviderType, cancellationToken);
            }
        }
        await importer.CompleteAsync(cancellationToken);
    }

    public static string StageName(PostgreSqlWriteTable table) => StageName(table.Target);

    public static string StageName(TableAddress table) =>
        PostgreSqlIdentifier.Qualified(
            "datapitcher",
            "stage_"
                + Convert
                    .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table.Schema + "\u001f" + table.Name)))
                    .ToLowerInvariant()
        );

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
