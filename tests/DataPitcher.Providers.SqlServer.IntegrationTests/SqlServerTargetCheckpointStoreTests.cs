using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerTargetCheckpointStoreTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task AdvanceAsync_RecordsTheBatchAndRejectsASupersededFence()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var old = SqlServerTransferTestData.Context();
        var current = old with { FenceToken = 2 };
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        await store.InitializeAsync(old, CancellationToken.None);
        await using var connection = new SqlConnection(scope.TargetConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await store.AdvanceAsync(
            connection,
            transaction,
            old,
            SqlServerTransferTestData.Table(),
            SqlServerTransferTestData.Batch(0, (1, "one")),
            1,
            1,
            0,
            CancellationToken.None
        );
        await transaction.CommitAsync(CancellationToken.None);
        var checkpoint = await store.ReadAsync(old.JobId, old.RunId, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(0, checkpoint!.LastBatchSequence);
        Assert.Equal(1, checkpoint.CumulativeAffected);

        await store.InitializeAsync(current, CancellationToken.None);
        await using var stale = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SqlServerFenceLostException>(() =>
            store.AdvanceAsync(
                connection,
                stale,
                old,
                SqlServerTransferTestData.Table(),
                SqlServerTransferTestData.Batch(1, (2, "two")),
                1,
                1,
                0,
                CancellationToken.None
            )
        );
        await stale.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitializeAsync_WhenManifestHashDiffersFromTheStoredCheckpoint_ThrowsWithoutChangingIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        var context = SqlServerTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        var resealed = context with { ManifestHash = "different-manifest-hash" };
        await Assert.ThrowsAsync<SqlServerManifestMismatchException>(() =>
            store.InitializeAsync(resealed, CancellationToken.None)
        );
        Assert.Equal(
            "sealed-manifest-hash",
            (await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None))!.ManifestHash
        );
    }

    [Fact]
    public async Task InitializeAsync_CalledTwiceWithTheSameFenceToken_IsIdempotent()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        var context = SqlServerTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        await store.InitializeAsync(context, CancellationToken.None);
        var checkpoint = await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None);
        Assert.Equal(1, checkpoint!.FenceToken);
        Assert.Equal(-1, checkpoint.LastBatchSequence);
    }

    [Fact]
    public async Task InitializeAsync_WhenAnOlderFenceArrivesAfterANewerOne_ThrowsFenceLost()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        var older = SqlServerTransferTestData.Context();
        var newer = older with { FenceToken = 5 };
        await store.InitializeAsync(older, CancellationToken.None);
        await store.InitializeAsync(newer, CancellationToken.None);
        await Assert.ThrowsAsync<SqlServerFenceLostException>(() =>
            store.InitializeAsync(older, CancellationToken.None)
        );
    }

    [Fact]
    public async Task InitializeAsync_WhenTheCheckpointFenceUpdateDoesNotAdvance_ThrowsFenceLost()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        var older = SqlServerTransferTestData.Context();
        await store.InitializeAsync(older, CancellationToken.None);
        await scope.ExecuteTargetAsync(
            "CREATE TRIGGER [datapitcher].[suppress_checkpoint_fence] ON [datapitcher].[transfer_checkpoints] INSTEAD OF UPDATE AS RETURN;"
        );
        await Assert.ThrowsAsync<SqlServerFenceLostException>(() =>
            store.InitializeAsync(older with { FenceToken = 2 }, CancellationToken.None)
        );
    }

    [Fact]
    public async Task InitializeAsync_WhenTheCheckpointFenceUpdateRemovesTheCheckpoint_ThrowsFenceLost()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);
        var older = SqlServerTransferTestData.Context();
        await store.InitializeAsync(older, CancellationToken.None);
        await scope.ExecuteTargetAsync(
            "CREATE TRIGGER [datapitcher].[remove_checkpoint_fence] ON [datapitcher].[transfer_checkpoints] INSTEAD OF UPDATE AS DELETE [datapitcher].[transfer_checkpoints] WHERE job_id IN (SELECT job_id FROM deleted) AND run_id IN (SELECT run_id FROM deleted);"
        );
        await Assert.ThrowsAsync<SqlServerFenceLostException>(() =>
            store.InitializeAsync(older with { FenceToken = 2 }, CancellationToken.None)
        );
    }

    [Fact]
    public async Task InitializeAsync_WhenCheckpointTablePredatesLastTable_AddsTheColumn()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SCHEMA [datapitcher]");
        await scope.ExecuteTargetAsync(
            "CREATE TABLE [datapitcher].[transfer_checkpoints] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,last_batch_sequence bigint NOT NULL,last_stable_key varbinary(max) NOT NULL,cumulative_affected bigint NOT NULL,cumulative_inserts bigint NOT NULL,cumulative_updates bigint NOT NULL,manifest_hash nvarchar(128) NOT NULL,fence_token bigint NOT NULL,PRIMARY KEY(job_id,run_id));"
        );
        var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString);

        await store.InitializeAsync(SqlServerTransferTestData.Context(), CancellationToken.None);

        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT CASE WHEN COL_LENGTH(N'datapitcher.transfer_checkpoints', N'last_table') IS NULL THEN 0 ELSE 1 END"
            )
        );
    }
}
