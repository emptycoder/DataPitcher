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

    [Fact]
    public void Build_WhenLimitIsNotPositive_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlKeysetSeek.Build(PostgreSqlTransferTestData.TextKeyTable("dp"), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 0));
    }

    [Fact]
    public void Build_WithACompositeKey_OrsEachPrefixEqualityAndOmitsCollateForNonTextColumns()
    {
        var table = new PostgreSqlWriteTable(new("dp", "two_key_rows"), [
            new("region", "text", NpgsqlTypes.NpgsqlDbType.Text, true, false, false, false, "C"),
            new("id", "integer", NpgsqlTypes.NpgsqlDbType.Integer, true, false, false, false, null)
        ]);
        var seek = PostgreSqlKeysetSeek.Build(table, new DataPitcher.Core.Identity.StableKey([new("region", "east"), new("id", 5)]), 10);
        Assert.Contains("WHERE (s.\"region\" COLLATE \"C\">@k0 OR s.\"region\" COLLATE \"C\"=@k0 AND s.\"id\">@k1)", seek.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY s.\"region\" COLLATE \"C\", s.\"id\"", seek.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheBatchAppliesSuccessfully_ReturnsTheCommitSummaryAndWritesTheMirrorOnce()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context();
        var mirror = new RecordingMirror();
        var executor = new PostgreSqlTransferExecutor(scope.Target, mirror, new PassBarrier());
        var commit = await executor.ExecuteAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        Assert.Equal(0, commit.Sequence);
        Assert.Equal(1, commit.Affected);
        Assert.Equal(1, commit.Inserts);
        Assert.Equal(0, commit.Updates);
        Assert.Equal(1, mirror.Writes);
    }

    [Fact]
    public async Task RecoverAsync_WhenTheRunWasNeverInitialized_ThrowsInvalidOperationException()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var executor = new PostgreSqlTransferExecutor(scope.Target, new RecordingMirror(), new PassBarrier());
        var initialized = PostgreSqlTransferTestData.Context();
        await executor.InitializeAsync(initialized, CancellationToken.None);
        var neverInitialized = PostgreSqlTransferTestData.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecoverAsync(neverInitialized, PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None));
    }

    [Fact]
    public async Task RecoverAsync_WhenTheManifestHashDiffersFromTheCheckpoint_ThrowsManifestMismatch()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var context = PostgreSqlTransferTestData.Context();
        var executor = new PostgreSqlTransferExecutor(scope.Target, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(context, CancellationToken.None);
        var resealed = context with { ManifestHash = "different-manifest-hash" };
        await Assert.ThrowsAsync<PostgreSqlManifestMismatchException>(() => executor.RecoverAsync(resealed, PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None));
    }

    [Fact]
    public async Task RecoverAsync_WhenNoBatchHasEverCommitted_ResumesAtSequenceZeroWithNoAfterKey()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var context = PostgreSqlTransferTestData.Context();
        var executor = new PostgreSqlTransferExecutor(scope.Target, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(context, CancellationToken.None);
        var resume = await executor.RecoverAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None);
        Assert.Equal(0, resume.NextBatchSequence);
        Assert.Null(resume.AfterStableKey);
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
