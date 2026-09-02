using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerTransferRecoveryTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public void Build_UsesLexicographicBinaryCollationKeysetSeekingWithoutOffset()
    {
        var query = SqlServerKeysetSeek.Build(SqlServerTransferTestData.TextKeyTable(), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 100);
        Assert.Contains("TOP (@limit)", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COLLATE Latin1_General_100_BIN2", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenLimitIsNotPositive_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerKeysetSeek.Build(SqlServerTransferTestData.TextKeyTable(), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 0));
    }

    [Fact]
    public void Build_WithACompositeKey_OrsEachPrefixEqualityAndOmitsCollateForNonTextColumns()
    {
        var table = new SqlServerWriteTable(new DataPitcher.Core.Plans.TableAddress("dbo", "two_key_rows"), [
            new("region", "nvarchar(64)", typeof(string), System.Data.SqlDbType.NVarChar, true, false, false, false, false, "Latin1_General_100_BIN2"),
            new("id", "int", typeof(int), System.Data.SqlDbType.Int, true, false, false, false, false, null)
        ]);
        var query = SqlServerKeysetSeek.Build(table, new DataPitcher.Core.Identity.StableKey([new("region", "east"), new("id", 5)]), 10);
        Assert.Contains("(s.[region] COLLATE Latin1_General_100_BIN2>@k0 OR s.[region] COLLATE Latin1_General_100_BIN2=@k0 AND s.[id]>@k1)", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY s.[region] COLLATE Latin1_General_100_BIN2,s.[id]", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenATextStableKeyHasNoCatalogCollation_ThrowsInvalidOperationException()
    {
        var table = new SqlServerWriteTable(new DataPitcher.Core.Plans.TableAddress("dbo", "rows"), [new("code", "nvarchar(64)", typeof(string), System.Data.SqlDbType.NVarChar, true, false, false, false, false, null)]);
        Assert.Throws<InvalidOperationException>(() => SqlServerKeysetSeek.Build(table, new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 10));
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessDiesAfterTargetCommit_RecoversIfAndOnlyIfTheCheckpointAdvanced()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);");
        var context = SqlServerTransferTestData.Context();
        var mirror = new RecordingMirror();
        var barrier = new CrashBarrier();
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, mirror, barrier);
        var running = executor.ExecuteAsync(context, SqlServerTransferTestData.Table(), SqlServerTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        await barrier.Reached.Task;
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.transfer_rows"));
        Assert.Equal(0, mirror.Writes);
        barrier.Crash.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);

        var resume = await executor.RecoverAsync(context, SqlServerTransferTestData.Table(), CancellationToken.None);
        Assert.Equal(1, resume.NextBatchSequence);
        Assert.Equal(1, resume.AfterStableKey!.Components.Single().Value);
        Assert.Equal(1, mirror.Writes);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerFenceIsStale_RollsBackBusinessRowsDeterministically()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);");
        var stale = SqlServerTransferTestData.Context();
        var current = stale with { FenceToken = 2 };
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(stale, CancellationToken.None);
        await executor.InitializeAsync(current, CancellationToken.None);
        await Assert.ThrowsAsync<SqlServerFenceLostException>(() => executor.ExecuteAsync(stale, SqlServerTransferTestData.Table(), SqlServerTransferTestData.Batch(0, (1, "one")), CancellationToken.None));
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.transfer_rows"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheBatchAppliesSuccessfully_ReturnsTheCommitSummaryAndWritesTheMirrorOnce()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);");
        var context = SqlServerTransferTestData.Context();
        var mirror = new RecordingMirror();
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, mirror, new PassBarrier());
        var commit = await executor.ExecuteAsync(context, SqlServerTransferTestData.Table(), SqlServerTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        Assert.Equal(0, commit.Sequence);
        Assert.Equal(1, commit.Affected);
        Assert.Equal(1, commit.Inserts);
        Assert.Equal(0, commit.Updates);
        Assert.Equal(1, mirror.Writes);
    }

    [Fact]
    public async Task RecoverAsync_WhenTheRunWasNeverInitialized_ThrowsInvalidOperationException()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, new RecordingMirror(), new PassBarrier());
        var initialized = SqlServerTransferTestData.Context();
        await executor.InitializeAsync(initialized, CancellationToken.None);
        var neverInitialized = SqlServerTransferTestData.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecoverAsync(neverInitialized, SqlServerTransferTestData.Table(), CancellationToken.None));
    }

    [Fact]
    public async Task RecoverAsync_WhenTheManifestHashDiffersFromTheCheckpoint_ThrowsManifestMismatch()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var context = SqlServerTransferTestData.Context();
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(context, CancellationToken.None);
        var resealed = context with { ManifestHash = "different-manifest-hash" };
        await Assert.ThrowsAsync<SqlServerManifestMismatchException>(() => executor.RecoverAsync(resealed, SqlServerTransferTestData.Table(), CancellationToken.None));
    }

    [Fact]
    public async Task RecoverAsync_WhenNoBatchHasEverCommitted_ResumesAtSequenceZeroWithNoAfterKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var context = SqlServerTransferTestData.Context();
        var executor = new SqlServerTransferExecutor(scope.TargetConnectionString, new RecordingMirror(), new PassBarrier());
        await executor.InitializeAsync(context, CancellationToken.None);
        var resume = await executor.RecoverAsync(context, SqlServerTransferTestData.Table(), CancellationToken.None);
        Assert.Equal(0, resume.NextBatchSequence);
        Assert.Null(resume.AfterStableKey);
    }

    private sealed class RecordingMirror : ISqlServerDerivedCheckpointMirror
    {
        public int Writes { get; private set; }

        public Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class CrashBarrier : ISqlServerAfterTargetCommitBarrier
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Crash { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Reached.SetResult(true);
            await Crash.Task;
            throw new InvalidOperationException("simulated process death");
        }
    }

    private sealed class PassBarrier : ISqlServerAfterTargetCommitBarrier
    {
        public Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
