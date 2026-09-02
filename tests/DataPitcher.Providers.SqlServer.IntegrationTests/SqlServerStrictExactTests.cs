using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerStrictExactTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task EnsureAvailableAsync_WhenThePlannedTargetHasAnEnabledTrigger_RefusesStrictExact()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64));");
        await scope.ExecuteTargetAsync("CREATE TRIGGER dbo.transfer_trigger ON dbo.transfer_rows AFTER INSERT AS SELECT 1;");
        await Assert.ThrowsAsync<SqlServerStrictExactBlockedException>(() => new SqlServerStrictExact(scope.TargetConnectionString).EnsureAvailableAsync(SqlServerTransferTestData.Table(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_WhenThePlannedTargetHasAnInboundCascade_RefusesStrictExact()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64)); CREATE TABLE dbo.transfer_children (id int PRIMARY KEY,parent_id int REFERENCES dbo.transfer_rows(id) ON UPDATE CASCADE);");
        await Assert.ThrowsAsync<SqlServerStrictExactBlockedException>(() => new SqlServerStrictExact(scope.TargetConnectionString).EnsureAvailableAsync(SqlServerTransferTestData.Table(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetHasNoSideEffects_AllowsStrictExact()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64));");
        await new SqlServerStrictExact(scope.TargetConnectionString).EnsureAvailableAsync(SqlServerTransferTestData.Table(), CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_AfterCommit_RequiresAffectedKeysToEqualThePlannedManifest()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64));");
        var context = SqlServerTransferTestData.Context();
        var table = SqlServerTransferTestData.Table();
        var strict = new SqlServerStrictExact(scope.TargetConnectionString);
        await strict.RecordPlannedAsync(context, table, [new StableKey([new KeyComponent("id", 1)])], CancellationToken.None);
        await new SqlServerTransferExecutor(scope.TargetConnectionString, new Mirror(), new Barrier()).ExecuteAsync(context, table, SqlServerTransferTestData.Batch(0, (1, "one")), CancellationToken.None);

        await strict.VerifyAsync(context, CancellationToken.None);
        await strict.RecordPlannedAsync(context, table, [new StableKey([new KeyComponent("id", 2)])], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.VerifyAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_WhenNeitherManifestNorAffectedKeysContainRows_Passes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var context = SqlServerTransferTestData.Context();
        var strict = new SqlServerStrictExact(scope.TargetConnectionString);
        await strict.RecordPlannedAsync(context, SqlServerTransferTestData.Table(), [], CancellationToken.None);
        await strict.VerifyAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task RealignAsync_DiscoversTheIdentityAndAdvancesPastExplicitKeys()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(1,1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF;");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(11L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'next')"));
    }

    [Fact]
    public async Task RealignAsync_WhenThePositiveIdentityIsAlreadyAhead_DoesNotRewindIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(1,1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF; DBCC CHECKIDENT ('dbo.sequence_rows', RESEED, 50);");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(51L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'ahead')"));
    }

    [Fact]
    public async Task RealignAsync_WhenThePositiveIdentityIsBehindTheOccupiedMaximum_ReseedsIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(1,1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF; DBCC CHECKIDENT ('dbo.sequence_rows', RESEED, 0);");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(11L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'reseeded')"));
    }

    [Fact]
    public async Task RealignAsync_WhenAnUnissuedPositiveIdentityIsAlreadyAhead_DoesNotRewindIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(100,1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF;");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(100L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'ahead')"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheIdentityDecreases_AdvancesPastTheOccupiedMinimum()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(-1,-1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (-10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF;");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(-11L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'next')"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheNegativeIdentityIsBehindTheOccupiedMinimum_ReseedsIt()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(-1,-1) PRIMARY KEY, code nvarchar(64) NOT NULL); SET IDENTITY_INSERT dbo.sequence_rows ON; INSERT dbo.sequence_rows (id,code) VALUES (-10,N'ten'); SET IDENTITY_INSERT dbo.sequence_rows OFF; DBCC CHECKIDENT ('dbo.sequence_rows', RESEED, 0);");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(-11L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'reseeded')"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheColumnIsNotAnIdentity_LeavesItsExistingValuesAlone()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint PRIMARY KEY, code nvarchar(64) NOT NULL); INSERT dbo.sequence_rows VALUES (10,N'ten');");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(10L, await scope.ScalarTargetAsync<long>("SELECT id FROM dbo.sequence_rows"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheIdentityTableIsEmpty_DoesNotAdvanceTheGenerator()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE dbo.sequence_rows (id bigint IDENTITY(1,1) PRIMARY KEY, code nvarchar(64) NOT NULL);");
        await new SqlServerIdentityRealigner(scope.TargetConnectionString).RealignAsync(SequenceTable(), "id", CancellationToken.None);
        Assert.Equal(1L, await scope.ScalarTargetAsync<long>("INSERT dbo.sequence_rows (code) OUTPUT INSERTED.id VALUES (N'first')"));
    }

    private static SqlServerWriteTable SequenceTable() => new(new DataPitcher.Core.Plans.TableAddress("dbo", "sequence_rows"), [
        new("id", "bigint", typeof(long), System.Data.SqlDbType.BigInt, true, true, false, false, false, null),
        new("code", "nvarchar(64)", typeof(string), System.Data.SqlDbType.NVarChar, false, false, false, false, false, "Latin1_General_100_BIN2")
    ]);

    private sealed class Mirror : ISqlServerDerivedCheckpointMirror
    {
        public Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Barrier : ISqlServerAfterTargetCommitBarrier
    {
        public Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
