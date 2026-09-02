using DataPitcher.Core.Identity;
using DataPitcher.Providers.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlStrictExactTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlStrictExactTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetHasUserTrigger_RefusesStrictExact()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL); CREATE FUNCTION transfer_notice() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$; CREATE TRIGGER transfer_trigger BEFORE INSERT ON transfer_rows FOR EACH ROW EXECUTE FUNCTION transfer_notice();");

        await Assert.ThrowsAsync<PostgreSqlStrictExactBlockedException>(() => new PostgreSqlStrictExact(scope.Target).EnsureAvailableAsync(TransferTable(scope.Schema), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetHasRewriteRule_RefusesStrictExact()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL); CREATE RULE transfer_rule AS ON INSERT TO transfer_rows DO INSTEAD NOTHING;");

        await Assert.ThrowsAsync<PostgreSqlStrictExactBlockedException>(() => new PostgreSqlStrictExact(scope.Target).EnsureAvailableAsync(TransferTable(scope.Schema), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetIsReferencedByCascadingUpdateConstraint_RefusesStrictExact()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL); CREATE TABLE transfer_children (id integer PRIMARY KEY, parent_id integer NOT NULL REFERENCES transfer_rows(id) ON UPDATE CASCADE);");

        await Assert.ThrowsAsync<PostgreSqlStrictExactBlockedException>(() => new PostgreSqlStrictExact(scope.Target).EnsureAvailableAsync(TransferTable(scope.Schema), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetHasNoSideEffects_AllowsStrictExact()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");

        await new PostgreSqlStrictExact(scope.Target).EnsureAvailableAsync(TransferTable(scope.Schema), CancellationToken.None);
    }

    [Fact]
    public async Task VerifyAsync_AfterCommit_RequiresAffectedKeysToEqualThePlannedManifest()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context();
        var table = TransferTable(scope.Schema);
        var strict = new PostgreSqlStrictExact(scope.Target);
        await strict.RecordPlannedAsync(context, table, [new StableKey([new KeyComponent("id", 1)])], CancellationToken.None);
        await new PostgreSqlTransferExecutor(scope.Target, new Mirror(), new Barrier()).ExecuteAsync(context, table, PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None);

        await strict.VerifyAsync(context, CancellationToken.None);
        await strict.RecordPlannedAsync(context, table, [new StableKey([new KeyComponent("id", 2)])], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.VerifyAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_WhenNeitherManifestNorAffectedKeysContainRows_Passes()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var context = PostgreSqlTransferTestData.Context();
        var strict = new PostgreSqlStrictExact(scope.Target);

        await strict.RecordPlannedAsync(context, TransferTable(scope.Schema), [], CancellationToken.None);
        await strict.VerifyAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task RealignAsync_DiscoversTheOwnedSequenceAndAdvancesPastExplicitKeys()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE unexpected_generator AS bigint START WITH 1; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('unexpected_generator'), code text NOT NULL); ALTER SEQUENCE unexpected_generator OWNED BY sequence_rows.id; INSERT INTO sequence_rows (id,code) VALUES (10,'ten');");

        await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None);

        Assert.Equal(11L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('next') RETURNING id"));
    }

    [Fact]
    public async Task RealignAsync_WhenThePositiveSequenceIsAlreadyAhead_DoesNotRewindIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE unexpected_generator AS bigint START WITH 1; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('unexpected_generator'), code text NOT NULL); ALTER SEQUENCE unexpected_generator OWNED BY sequence_rows.id; INSERT INTO sequence_rows (id,code) VALUES (10,'ten');");
        await using (var connection = await scope.Target.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("SELECT setval('unexpected_generator',50,true)", connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None);

        Assert.Equal(51L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('ahead') RETURNING id"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheOwnedSequenceDecreases_AdvancesPastTheOccupiedMinimum()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE backwards_generator AS bigint START WITH -1 INCREMENT BY -1; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('backwards_generator'), code text NOT NULL); ALTER SEQUENCE backwards_generator OWNED BY sequence_rows.id; INSERT INTO sequence_rows (id,code) VALUES (-10,'ten');");

        await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None);

        Assert.Equal(-11L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('next') RETURNING id"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheOwnedSequenceIsSharedByAnotherColumn_RefusesToAdjustIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE shared_generator AS bigint START WITH 1; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('shared_generator'), code text NOT NULL); ALTER SEQUENCE shared_generator OWNED BY sequence_rows.id; CREATE TABLE shared_rows (id bigint PRIMARY KEY DEFAULT nextval('shared_generator')); INSERT INTO sequence_rows (id,code) VALUES (10,'ten');");

        await Assert.ThrowsAsync<NotSupportedException>(() => new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None));
    }

    [Fact]
    public async Task RealignAsync_WhenTheOwnedSequenceCycles_RefusesToAdjustIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE cycling_generator AS bigint START WITH 1 CYCLE; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('cycling_generator'), code text NOT NULL); ALTER SEQUENCE cycling_generator OWNED BY sequence_rows.id;");

        await Assert.ThrowsAsync<NotSupportedException>(() => new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None));
    }

    [Fact]
    public async Task RealignAsync_WhenTheOwnedSequenceTableIsEmpty_DoesNotAdvanceTheGenerator()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE SEQUENCE empty_generator AS bigint START WITH 1; CREATE TABLE sequence_rows (id bigint PRIMARY KEY DEFAULT nextval('empty_generator'), code text NOT NULL); ALTER SEQUENCE empty_generator OWNED BY sequence_rows.id;");

        await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None);

        Assert.Equal(1L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('first') RETURNING id"));
    }

    [Fact]
    public async Task RealignAsync_WhenTheColumnHasNoOwnedSequence_LeavesItsExistingValuesAlone()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE sequence_rows (id bigint PRIMARY KEY, code text NOT NULL); INSERT INTO sequence_rows VALUES (10,'ten');");

        await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(SequenceTable(scope.Schema), "id", CancellationToken.None);

        Assert.Equal(10L, await scope.ScalarTargetAsync<long>("SELECT id FROM sequence_rows"));
    }

    private static PostgreSqlWriteTable TransferTable(string schema) => new(new(schema, "transfer_rows"), [new("id", "integer", NpgsqlDbType.Integer, true, false, false, false, null), new("code", "text", NpgsqlDbType.Text, false, false, false, false, "C")]);

    private static PostgreSqlWriteTable SequenceTable(string schema) => new(new(schema, "sequence_rows"), [new("id", "bigint", NpgsqlDbType.Bigint, true, false, false, false, null), new("code", "text", NpgsqlDbType.Text, false, false, false, false, "C")]);

    private sealed class Mirror : IDerivedCheckpointMirror
    {
        public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Barrier : IAfterTargetCommitBarrier
    {
        public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
