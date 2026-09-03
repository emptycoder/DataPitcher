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
                $"SELECT COUNT(*) FROM [datapitcher].[transfer_affected_keys] WHERE job_id='{context.JobId}'"
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
