using DataPitcher.Providers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlTransferRecoveryTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlTransferRecoveryTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExecuteAsync_WhenProcessDiesAfterTargetCommit_RecoversIfAndOnlyIfTheCheckpointAdvanced()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context();
        var mirror = new RecordingMirror();
        var barrier = new CrashBarrier();
        var executor = new PostgreSqlTransferExecutor(scope.Target, mirror, barrier);
        var running = executor.ExecuteAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        await barrier.Reached.Task;
        Assert.Equal(1L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows"));
        Assert.Equal(0, mirror.Writes);
        barrier.Crash.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        var resume = await executor.RecoverAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None);
        Assert.Equal(1, resume.NextBatchSequence);
        Assert.Equal(1, resume.AfterStableKey!.Components.Single().Value);
        Assert.Equal(1, mirror.Writes);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerFenceIsStale_RollsBackBusinessRowsDeterministically()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var stale = PostgreSqlTransferTestData.Context();
        var current = stale with { FenceToken = 2 };
        var executor = new PostgreSqlTransferExecutor(scope.Target, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(stale, CancellationToken.None);
        await executor.InitializeAsync(current, CancellationToken.None);
        await Assert.ThrowsAsync<PostgreSqlFenceLostException>(() => executor.ExecuteAsync(stale, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None));
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows"));
    }

    [Fact]
    public void Build_UsesCompositeKeysetSeekingAndCOrdinalTextWithoutOffset()
    {
        var seek = PostgreSqlKeysetSeek.Build(PostgreSqlTransferTestData.TextKeyTable("dp"), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 100);
        Assert.Contains("WHERE (s.\"code\" COLLATE \"C\">@k0)", seek.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @limit", seek.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", seek.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingMirror : IDerivedCheckpointMirror
    {
        public int Writes { get; private set; }

        public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class CrashBarrier : IAfterTargetCommitBarrier
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Crash { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Reached.SetResult(true);
            await Crash.Task;
            throw new InvalidOperationException("simulated process death");
        }
    }

    private sealed class PassBarrier : IAfterTargetCommitBarrier
    {
        public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
