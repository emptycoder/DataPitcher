using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlTargetCheckpointStoreTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlTargetCheckpointStoreTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AdvanceAsync_WritesBatchAndFenceOnlyWhenTheTargetTokenMatches()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var store = new PostgreSqlTargetCheckpointStore(scope.Target);
        var context = PostgreSqlTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.AdvanceAsync(
            connection,
            transaction,
            context,
            PostgreSqlTransferTestData.Table(scope.Schema),
            PostgreSqlTransferTestData.Batch(0, (1, "a")),
            1,
            1,
            0,
            0,
            CancellationToken.None
        );
        await transaction.CommitAsync();
        var checkpoint = await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(0, checkpoint!.LastBatchSequence);
        Assert.Equal(1, checkpoint.CumulativeAffected);
        Assert.Equal(1, checkpoint.FenceToken);
    }

    [Fact]
    public async Task AdvanceAsync_WhenNewerWorkerOwnsTheFence_ThrowsWithoutAdvancing()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var store = new PostgreSqlTargetCheckpointStore(scope.Target);
        var stale = PostgreSqlTransferTestData.Context();
        var current = stale with { FenceToken = 2 };
        await store.InitializeAsync(stale, CancellationToken.None);
        await store.InitializeAsync(current, CancellationToken.None);
        await using var connection = await scope.Target.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<PostgreSqlFenceLostException>(() =>
            store.AdvanceAsync(
                connection,
                transaction,
                stale,
                PostgreSqlTransferTestData.Table(scope.Schema),
                PostgreSqlTransferTestData.Batch(0, (1, "a")),
                1,
                1,
                0,
                0,
                CancellationToken.None
            )
        );
        await transaction.RollbackAsync();
        Assert.Equal(2, (await store.ReadAsync(stale.JobId, stale.RunId, CancellationToken.None))!.FenceToken);
    }

    [Fact]
    public async Task InitializeAsync_WhenManifestHashDiffersFromTheStoredCheckpoint_ThrowsWithoutChangingIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var store = new PostgreSqlTargetCheckpointStore(scope.Target);
        var context = PostgreSqlTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        var resealed = context with { ManifestHash = "different-manifest-hash" };
        await Assert.ThrowsAsync<PostgreSqlManifestMismatchException>(() =>
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
        await using var scope = await _fixture.CreateScopeAsync();
        var store = new PostgreSqlTargetCheckpointStore(scope.Target);
        var context = PostgreSqlTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        await store.InitializeAsync(context, CancellationToken.None);
        var checkpoint = await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None);
        Assert.Equal(1, checkpoint!.FenceToken);
        Assert.Equal(-1, checkpoint.LastBatchSequence);
    }
}
