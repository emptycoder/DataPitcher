using System.Data;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerBatchExecutionTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task StageAsync_WhenTheSecondNativeCopyFails_RollbackLeavesNoBusinessRowOrCheckpointAdvance()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(2) NOT NULL);"
        );
        var context = SqlServerTransferTestData.Context();
        var table = new SqlServerWriteTable(
            SqlServerTransferTestData.Table().Target,
            [
                new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
                new(
                    "code",
                    "nvarchar(2)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "Latin1_General_100_BIN2"
                ),
            ]
        );
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        await store.InitializeAsync(context, CancellationToken.None);

        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        var writer = new SqlServerBatchStageWriter();
        await writer.StageAsync(
            connection,
            transaction,
            context,
            table,
            SqlServerTransferTestData.Batch(0, (1, "ok")),
            CancellationToken.None
        );
        await Assert.ThrowsAnyAsync<Exception>(() =>
            writer.StageAsync(
                connection,
                transaction,
                context,
                table,
                SqlServerTransferTestData.Batch(1, (2, "too-long")),
                CancellationToken.None
            )
        );
        await transaction.RollbackAsync(CancellationToken.None);

        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.transfer_rows"));
        var checkpoint = await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(-1, checkpoint!.LastBatchSequence);
    }

    [Fact]
    public async Task BackfillAsync_WhenItIsTheFirstWriteOfTheRun_EnsuresTheLedgerAndTouchesOnlyRowsTheRunWrote()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY, parent_id int NULL); INSERT dbo.transfer_rows VALUES (1, NULL);"
                + " CREATE TABLE dbo.coded_rows (code nvarchar(64) COLLATE Latin1_General_100_BIN2 PRIMARY KEY, parent_id int NULL); INSERT dbo.coded_rows VALUES (N'a', NULL);"
        );
        var context = SqlServerTransferTestData.Context();
        var numeric = new SqlServerWriteTable(
            new TableAddress("dbo", "transfer_rows"),
            [
                new("id", "int", typeof(int), System.Data.SqlDbType.Int, true, false, false, false, false, null),
                new("parent_id", "int", typeof(int), System.Data.SqlDbType.Int, false, false, false, false, true, null),
            ]
        );
        var coded = new SqlServerWriteTable(
            new TableAddress("dbo", "coded_rows"),
            [
                new(
                    "code",
                    "nvarchar(64)",
                    typeof(string),
                    System.Data.SqlDbType.NVarChar,
                    true,
                    false,
                    false,
                    false,
                    false,
                    "Latin1_General_100_BIN2"
                ),
                new("parent_id", "int", typeof(int), System.Data.SqlDbType.Int, false, false, false, false, true, null),
            ]
        );
        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var applier = new SqlServerBatchApplier();
        var numericAffected = await applier.BackfillAsync(
            connection,
            transaction,
            context,
            numeric,
            Batch(numeric, ("id", 1, 5), ("id", 2, null)),
            CancellationToken.None
        );
        var codedAffected = await applier.BackfillAsync(
            connection,
            transaction,
            context,
            coded,
            Batch(coded, ("code", "a", 7)),
            CancellationToken.None
        );
        await transaction.CommitAsync();

        // Neither row is in the run's ledger, so nothing is filled in; the ledger itself now exists.
        Assert.Equal(0L, numericAffected);
        Assert.Equal(0L, codedAffected);
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.transfer_rows WHERE parent_id IS NULL")
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.coded_rows WHERE parent_id IS NULL")
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'transfer_affected_keys' AND schema_id = SCHEMA_ID('datapitcher')"
            )
        );
    }

    private static SqlServerTransferBatch Batch(
        SqlServerWriteTable table,
        params (string KeyColumn, object Key, object? Parent)[] rows
    ) =>
        new(
            0,
            rows.Select(row => new SqlServerTransferRow(
                new StableKey([new KeyComponent(row.KeyColumn, row.Key)]),
                new Dictionary<string, object?> { [row.KeyColumn] = row.Key, ["parent_id"] = row.Parent }
            )),
            new StableKey([new KeyComponent(rows[^1].KeyColumn, rows[^1].Key)]),
            SqlServerConflictPolicy.SkipExisting
        );

    [Theory]
    [InlineData(SqlServerConflictPolicy.InsertOnly, 2, 0, 2)]
    [InlineData(SqlServerConflictPolicy.SkipExisting, 1, 0, 1)]
    [InlineData(SqlServerConflictPolicy.Upsert, 2, 1, 1)]
    public async Task ApplyAsync_UsesSeparateStatementsAndRecordsOnlyCommittedAffectedKeys(
        SqlServerConflictPolicy policy,
        int affected,
        int updates,
        int inserts
    )
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);"
        );
        if (policy != SqlServerConflictPolicy.InsertOnly)
            await scope.ExecuteTargetAsync("INSERT dbo.transfer_rows VALUES (1,N'old');");
        var context = SqlServerTransferTestData.Context();
        var batch = SqlServerTransferTestData.Batch(0, (1, "new"), (2, "two"));
        batch = new SqlServerTransferBatch(batch.Sequence, batch.Rows, batch.LastStableKey, policy);

        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await new SqlServerBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            SqlServerTransferTestData.Table(),
            batch,
            CancellationToken.None
        );
        var result = await new SqlServerBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            SqlServerTransferTestData.Table(),
            batch,
            CancellationToken.None
        );
        Assert.Equal(affected, result.Affected);
        Assert.Equal(inserts, result.Inserts);
        Assert.Equal(updates, result.Updates);
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(
            affected,
            await scope.ScalarTargetAsync<int>(
                $"SELECT COUNT(*) FROM [datapitcher].[transfer_affected_keys] WHERE job_id='{context.JobId}' AND action_name<>'SKIP'"
            )
        );
        // Every planned key is accounted for: written, or recorded as already present.
        Assert.Equal(
            2,
            await scope.ScalarTargetAsync<int>(
                $"SELECT COUNT(*) FROM [datapitcher].[transfer_affected_keys] WHERE job_id='{context.JobId}'"
            )
        );
        Assert.Equal(
            2,
            await scope.ScalarTargetAsync<int>(
                $"SELECT COUNT(*) FROM [datapitcher].[transfer_write_manifest] WHERE job_id='{context.JobId}'"
            )
        );
    }

    [Fact]
    public async Task StageAsync_WhenAColumnValueIsNull_WritesNullThroughSqlBulkCopy()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL, note nvarchar(64) NULL);"
        );
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "transfer_rows"),
            [
                new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
                new(
                    "code",
                    "nvarchar(64)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "Latin1_General_100_BIN2"
                ),
                new(
                    "note",
                    "nvarchar(64)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    false,
                    false,
                    false,
                    false,
                    true,
                    "Latin1_General_100_BIN2"
                ),
            ]
        );
        var context = SqlServerTransferTestData.Context();
        var batch = new SqlServerTransferBatch(
            0,
            [
                new SqlServerTransferRow(
                    new StableKey([new KeyComponent("id", 1)]),
                    new Dictionary<string, object?>
                    {
                        ["id"] = 1,
                        ["code"] = "ok",
                        ["note"] = null,
                    }
                ),
            ],
            new StableKey([new KeyComponent("id", 1)]),
            SqlServerConflictPolicy.InsertOnly
        );

        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await new SqlServerBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await new SqlServerBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync(CancellationToken.None);

        Assert.True(
            await scope.ScalarTargetAsync<bool>(
                "SELECT CAST(CASE WHEN note IS NULL THEN 1 ELSE 0 END AS bit) FROM dbo.transfer_rows WHERE id=1"
            )
        );
    }

    [Fact]
    public async Task ApplyAsync_WhenAnIdentityColumnIsPresent_InsertsExplicitValuesAndAdvancesIdentity()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.identity_rows (id int IDENTITY PRIMARY KEY, code nvarchar(64) NOT NULL);"
        );
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "identity_rows"),
            [
                new("id", "int", typeof(int), SqlDbType.Int, true, true, false, false, false, null),
                new(
                    "code",
                    "nvarchar(64)",
                    typeof(string),
                    SqlDbType.NVarChar,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "Latin1_General_100_BIN2"
                ),
            ]
        );
        var context = SqlServerTransferTestData.Context();
        var batch = new SqlServerTransferBatch(
            0,
            [
                new SqlServerTransferRow(
                    new StableKey([new KeyComponent("id", 100)]),
                    new Dictionary<string, object?> { ["id"] = 100, ["code"] = "explicit" }
                ),
            ],
            new StableKey([new KeyComponent("id", 100)]),
            SqlServerConflictPolicy.InsertOnly
        );

        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await new SqlServerBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        var result = await new SqlServerBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(1, result.Inserts);
        Assert.Equal(100, await scope.ScalarTargetAsync<int>("SELECT id FROM dbo.identity_rows"));
    }

    [Fact]
    public async Task ApplyAsync_WhenUpsertHasNoMutableColumnsBesidesTheKey_SkipsTheUpdateStatement()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.key_only_rows (id int NOT NULL PRIMARY KEY);");
        var table = new SqlServerWriteTable(
            new TableAddress("dbo", "key_only_rows"),
            [new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null)]
        );
        await scope.ExecuteTargetAsync("INSERT dbo.key_only_rows VALUES (1);");
        var context = SqlServerTransferTestData.Context();
        var batch = new SqlServerTransferBatch(
            0,
            [
                new SqlServerTransferRow(
                    new StableKey([new KeyComponent("id", 1)]),
                    new Dictionary<string, object?> { ["id"] = 1 }
                ),
            ],
            new StableKey([new KeyComponent("id", 1)]),
            SqlServerConflictPolicy.Upsert
        );

        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await new SqlServerBatchStageWriter().StageAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        var result = await new SqlServerBatchApplier().ApplyAsync(
            connection,
            transaction,
            context,
            table,
            batch,
            CancellationToken.None
        );
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(0, result.Updates);
        Assert.Equal(0, result.Inserts);
        Assert.Equal(0, result.Affected);
    }

    [Fact]
    public void StageName_IsDeterministicForTheSameTargetTable()
    {
        var name1 = SqlServerBatchStageWriter.StageName(SqlServerTransferTestData.Table());
        var name2 = SqlServerBatchStageWriter.StageName(SqlServerTransferTestData.Table());
        Assert.Equal(name1, name2);
        Assert.StartsWith("[datapitcher].[stage_", name1, StringComparison.Ordinal);
    }
}
