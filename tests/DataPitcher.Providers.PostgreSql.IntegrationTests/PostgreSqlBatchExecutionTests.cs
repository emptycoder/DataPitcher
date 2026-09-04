using DataPitcher.Core.Identity;
using DataPitcher.Core.Transfer;
using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlBatchExecutionTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlBatchExecutionTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StageAsync_WhenSecondRowFailsBeforeComplete_AbortsCopyAndRecordsNoCheckpoint()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context();
        var table = PostgreSqlTransferTestData.Table(scope.Schema);
        var checkpoints = new PostgreSqlTargetCheckpointStore(scope.Target);
        await checkpoints.InitializeAsync(context, CancellationToken.None);
        var bad = new PostgreSqlTransferBatch(
            0,
            [
                new(
                    new DataPitcher.Core.Identity.StableKey([new("id", 1)]),
                    new Dictionary<string, object?> { ["id"] = 1, ["code"] = "ok" }
                ),
                new(
                    new DataPitcher.Core.Identity.StableKey([new("id", 2)]),
                    new Dictionary<string, object?> { ["id"] = "not-an-integer", ["code"] = "bad" }
                ),
            ],
            new([new("id", 2)]),
            PostgreSqlConflictPolicy.InsertOnly
        );
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new PostgreSqlBatchStageWriter().StageAsync(
                connection,
                transaction,
                context,
                table,
                bad,
                CancellationToken.None
            )
        );
        await transaction.RollbackAsync();
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows"));
        Assert.Equal(
            -1,
            (await checkpoints.ReadAsync(context.JobId, context.RunId, CancellationToken.None))!.LastBatchSequence
        );
    }

    [Fact]
    public async Task ApplyAsync_WhenARowCollidesWithADifferentTargetRowOnAnotherUniqueKey_StopsTheRunNamingBothRows()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL CONSTRAINT uq_transfer_rows_code UNIQUE); INSERT INTO transfer_rows VALUES (1, 'taken');"
        );
        var table = await new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(
            scope.Schema,
            "transfer_rows",
            ["id"],
            CancellationToken.None
        );
        var context = PostgreSqlTransferTestData.Context();
        var batch = PostgreSqlTransferTestData.Batch(0, (2, "taken"), (3, "free"));
        batch = new PostgreSqlTransferBatch(
            batch.Sequence,
            batch.Rows,
            batch.LastStableKey,
            PostgreSqlConflictPolicy.SkipExisting
        );
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new PostgreSqlBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );

        var exception = await Assert.ThrowsAsync<TargetRowConflictException>(() =>
            new PostgreSqlBatchApplier().ApplyAsync(
                connection,
                transaction,
                context,
                table,
                batch,
                CancellationToken.None
            )
        );
        await transaction.RollbackAsync();

        Assert.Equal(
            [
                ["code"],
            ],
            table.UniqueKeys.Select(key => key.Select(column => column.Name).ToArray())
        );
        Assert.Contains(
            "Row id=2 (source) -> id=1 (target) of "
                + scope.Schema
                + ".transfer_rows collides with a different target row on unique key (code)",
            exception.Message
        );
        Assert.Equal(1L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows"));
    }

    [Fact]
    public async Task BackfillAsync_WhenItIsTheFirstWriteOfTheRun_EnsuresTheLedgerAndTouchesOnlyRowsTheRunWrote()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE transfer_rows (id integer PRIMARY KEY, parent_id integer NULL); INSERT INTO transfer_rows VALUES (1, NULL);"
        );
        var context = PostgreSqlTransferTestData.Context();
        var table = new PostgreSqlWriteTable(
            new(scope.Schema, "transfer_rows"),
            [
                new("id", "integer", NpgsqlTypes.NpgsqlDbType.Integer, true, false, false, false, null),
                new("parent_id", "integer", NpgsqlTypes.NpgsqlDbType.Integer, false, false, false, false, null),
            ]
        );
        var batch = new PostgreSqlTransferBatch(
            0,
            [
                new PostgreSqlTransferRow(
                    new StableKey([new KeyComponent("id", 1)]),
                    new Dictionary<string, object?> { ["id"] = 1, ["parent_id"] = 5 }
                ),
                new PostgreSqlTransferRow(
                    new StableKey([new KeyComponent("id", 2)]),
                    new Dictionary<string, object?> { ["id"] = 2, ["parent_id"] = null }
                ),
            ],
            new StableKey([new KeyComponent("id", 2)]),
            PostgreSqlConflictPolicy.SkipExisting
        );
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var affected = await new PostgreSqlBatchApplier().BackfillAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync();

        // Neither row is in the run's ledger, so nothing is filled in; the ledger itself now exists.
        Assert.Equal(0L, affected);
        Assert.Equal(
            1L,
            await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows WHERE parent_id IS NULL")
        );
        Assert.True(
            await scope.ScalarTargetAsync<bool>("SELECT to_regclass('datapitcher.transfer_affected_keys') IS NOT NULL")
        );
    }

    [Theory]
    [InlineData(PostgreSqlConflictPolicy.InsertOnly, 2, 0, 2)]
    [InlineData(PostgreSqlConflictPolicy.SkipExisting, 1, 0, 1)]
    [InlineData(PostgreSqlConflictPolicy.Upsert, 2, 1, 1)]
    public async Task ApplyAsync_CapturesOnlyInsertedOrUpdatedKeysAfterTheCallerCommits(
        PostgreSqlConflictPolicy policy,
        int affected,
        int updates,
        int inserts
    )
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        if (policy != PostgreSqlConflictPolicy.InsertOnly)
            await scope.ExecuteTargetAsync("INSERT INTO transfer_rows VALUES (1,'old');");
        var table = PostgreSqlTransferTestData.Table(scope.Schema);
        var context = PostgreSqlTransferTestData.Context();
        var batch = PostgreSqlTransferTestData.Batch(0, (1, "new"), (2, "two"));
        batch = new PostgreSqlTransferBatch(batch.Sequence, batch.Rows, batch.LastStableKey, policy);
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var writer = new PostgreSqlBatchStageWriter();
        await writer.StageAsync(connection, transaction, context, table, batch, CancellationToken.None);
        var result = await new PostgreSqlBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        Assert.Equal(affected, result.Affected);
        Assert.Equal(updates, result.Updates);
        Assert.Equal(inserts, result.Inserts);
        await transaction.CommitAsync();
        Assert.Equal(
            (long)affected,
            await scope.ScalarTargetAsync<long>(
                $"SELECT count(*) FROM datapitcher.transfer_affected_keys WHERE job_id='{context.JobId}' AND action_name <> 'SKIP'"
            )
        );
        // Every planned key is accounted for: written, or recorded as already present.
        Assert.Equal(
            2L,
            await scope.ScalarTargetAsync<long>(
                $"SELECT count(*) FROM datapitcher.transfer_affected_keys WHERE job_id='{context.JobId}'"
            )
        );
        Assert.Equal(
            2L,
            await scope.ScalarTargetAsync<long>(
                $"SELECT count(*) FROM datapitcher.transfer_write_manifest WHERE job_id='{context.JobId}'"
            )
        );
    }

    [Fact]
    public async Task StageAsync_WhenAColumnValueIsNull_WritesNullThroughBinaryCopy()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL, note text NULL);"
        );
        var table = new PostgreSqlWriteTable(
            new(scope.Schema, "transfer_rows"),
            [
                new("id", "integer", NpgsqlTypes.NpgsqlDbType.Integer, true, false, false, false, null),
                new("code", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C"),
                new("note", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C"),
            ]
        );
        var context = PostgreSqlTransferTestData.Context();
        var batch = new PostgreSqlTransferBatch(
            0,
            [
                new(
                    new DataPitcher.Core.Identity.StableKey([new("id", 1)]),
                    new Dictionary<string, object?>
                    {
                        ["id"] = 1,
                        ["code"] = "ok",
                        ["note"] = null,
                    }
                ),
            ],
            new([new("id", 1)]),
            PostgreSqlConflictPolicy.InsertOnly
        );
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new PostgreSqlBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await new PostgreSqlBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync();
        Assert.True(await scope.ScalarTargetAsync<bool>("SELECT note IS NULL FROM transfer_rows WHERE id=1"));
    }

    [Fact]
    public async Task ApplyAsync_WhenAnIdentityAlwaysColumnIsPresent_InsertsExplicitValuesUsingOverridingSystemValue()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE identity_rows (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, code text NOT NULL);"
        );
        var table = new PostgreSqlWriteTable(
            new(scope.Schema, "identity_rows"),
            [
                new("id", "bigint", NpgsqlTypes.NpgsqlDbType.Bigint, true, false, false, true, null),
                new("code", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C"),
            ]
        );
        var context = PostgreSqlTransferTestData.Context();
        var batch = new PostgreSqlTransferBatch(
            0,
            [
                new(
                    new DataPitcher.Core.Identity.StableKey([new("id", 100L)]),
                    new Dictionary<string, object?> { ["id"] = 100L, ["code"] = "explicit" }
                ),
            ],
            new([new("id", 100L)]),
            PostgreSqlConflictPolicy.InsertOnly
        );
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new PostgreSqlBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        var result = await new PostgreSqlBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync();
        Assert.Equal(1, result.Inserts);
        Assert.Equal(100L, await scope.ScalarTargetAsync<long>("SELECT id FROM identity_rows"));
    }
}
