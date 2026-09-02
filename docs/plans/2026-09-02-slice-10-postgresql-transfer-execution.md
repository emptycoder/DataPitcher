# DataPitcher Slice 10: PostgreSQL Transfer Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute fenced PostgreSQL batches that atomically apply rows and the authoritative target resume checkpoint.

**Architecture:** The provider COPYs pipeline batches to target staging, applies/captures them, and checkpoints atomically. Recovery trusts the target checkpoint and resumes by keyset.

**Tech Stack:** .NET 10; Npgsql **10.0.3**; Testcontainers.PostgreSql **4.14.0**; `postgres:17-alpine`; xUnit.

---

## File Structure

- `src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferModels.cs`, `PostgreSqlTransferSchemaReader.cs`
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlTargetCheckpointStore.cs`, `PostgreSqlBatchStageWriter.cs`, `PostgreSqlBatchApplier.cs`
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferExecutor.cs`, `PostgreSqlKeysetSeek.cs`, `PostgreSqlStrictExact.cs`
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferTestData.cs`, `PostgreSqlTransferModelsTests.cs`, `PostgreSqlTargetCheckpointStoreTests.cs`
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlBatchExecutionTests.cs`, `PostgreSqlTransferRecoveryTests.cs`, `PostgreSqlStrictExactTests.cs`
- `docs/plans/2026-09-02-slice-10-postgresql-transfer-execution.md`

## Scope and Deferrals

The pipeline supplies `IAsyncEnumerable<PostgreSqlTransferBatch>` and owns source reads, SQL Server, sizing, and workers. Tests use separate containers.

Npgsql binary COPY is direct; ADR 0005 excludes degradable LINQ to DB `BulkCopy`. Every target value has catalog-derived `NpgsqlDbType`; unknown types block setup.

The target checkpoint holds job/run, sequence, encoded key, counts, manifest hash, and fence. It changes with business writes. Control gets only a post-commit write-only mirror.

StrictExact covers planned business-table INSERT/UPDATE keys, excluding `datapitcher` objects and unrelated writers. Triggers, rules, or cascades block it. SQL Server, pipeline, integrity scans, checksums, and cycle execution remain later.

Warnings are errors. Test every public member in its task. Only `scripts/test-all.sh` enforces merged 100 percent coverage.

## Tasks

### Task 1: Define transfer contracts and read target write metadata

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferModels.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferSchemaReader.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferTestData.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferModelsTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferModelsTests.cs`

- [ ] **Step 1: Write the failing transfer-contract and catalog tests.**

```csharp
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.PostgreSql;
using Xunit;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
public sealed class PostgreSqlTransferModelsTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;
    public PostgreSqlTransferModelsTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task ReadAsync_MapsEveryWritableColumnToAnExplicitProviderType()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text COLLATE \"C\" NOT NULL, stamp bigint NOT NULL, computed integer GENERATED ALWAYS AS (id + 1) STORED);");
        var table = await new PostgreSqlTransferSchemaReader(scope.Target).ReadAsync(scope.Schema, "transfer_rows", ["id"], CancellationToken.None);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Integer, table.Column("id").ProviderType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Text, table.Column("code").ProviderType);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Bigint, table.Column("stamp").ProviderType);
        Assert.True(table.Column("computed").IsGenerated);
        Assert.Equal("C", table.Column("code").Collation);
    }
    [Fact]
    public void WriteTable_ExcludesProtectedColumnsAndRoundTripsNativeStableKeys()
    {
        var table = PostgreSqlTransferTestData.Table("dp");
        Assert.Equal(["id", "code"], table.InsertColumns.Select(column => column.Name));
        Assert.Equal("code", Assert.Single(table.UpdateColumns).Name);
        var key = new StableKey([new KeyComponent("id", 7)]);
        Assert.Equal(key, PostgreSqlStableKeyCodec.Decode(PostgreSqlStableKeyCodec.Encode(key, table), table));
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the contract is absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTransferModelsTests"`

Expected: compilation fails with CS0246 stating that `PostgreSqlTransferSchemaReader` and `PostgreSqlWriteTable` could not be found.

- [ ] **Step 3: Add immutable contracts, explicit catalog mapping, and test builders.**

```csharp
using System.Buffers; using System.Buffers.Binary; using System.Text;
using DataPitcher.Core.Identity; using DataPitcher.Core.Plans; using NpgsqlTypes;
namespace DataPitcher.Providers.PostgreSql;
public enum PostgreSqlConflictPolicy { InsertOnly, SkipExisting, Upsert }
public sealed record PostgreSqlWriteColumn(string Name, string StoreType, NpgsqlDbType ProviderType, bool IsStableKey, bool IsGenerated, bool IsRowVersion, bool IsIdentityAlways, string? Collation);
public sealed class PostgreSqlWriteTable
{
    public PostgreSqlWriteTable(TableAddress target, IEnumerable<PostgreSqlWriteColumn> columns) { Target = target; Columns = Array.AsReadOnly(columns.ToArray()); StableKeyColumns = Array.AsReadOnly(Columns.Where(x => x.IsStableKey).ToArray()); InsertColumns = Array.AsReadOnly(Columns.Where(x => !x.IsGenerated && !x.IsRowVersion).ToArray()); UpdateColumns = Array.AsReadOnly(InsertColumns.Where(x => !x.IsStableKey && !x.IsIdentityAlways).ToArray()); if (StableKeyColumns.Count == 0) throw new ArgumentException("A write table requires a stable key."); }
    public TableAddress Target { get; } public IReadOnlyList<PostgreSqlWriteColumn> Columns { get; } public IReadOnlyList<PostgreSqlWriteColumn> StableKeyColumns { get; } public IReadOnlyList<PostgreSqlWriteColumn> InsertColumns { get; } public IReadOnlyList<PostgreSqlWriteColumn> UpdateColumns { get; }
    public PostgreSqlWriteColumn Column(string name) => Columns.Single(x => StringComparer.Ordinal.Equals(x.Name, name));
}
public sealed class PostgreSqlTransferRow { public PostgreSqlTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values) { StableKey = stableKey; Values = new Dictionary<string, object?>(values, StringComparer.Ordinal); } public StableKey StableKey { get; } public IReadOnlyDictionary<string, object?> Values { get; } }
public sealed class PostgreSqlTransferBatch { public PostgreSqlTransferBatch(long sequence, IEnumerable<PostgreSqlTransferRow> rows, StableKey lastStableKey, PostgreSqlConflictPolicy policy) { Sequence = sequence; Rows = Array.AsReadOnly(rows.ToArray()); LastStableKey = lastStableKey; Policy = policy; } public long Sequence { get; } public IReadOnlyList<PostgreSqlTransferRow> Rows { get; } public StableKey LastStableKey { get; } public PostgreSqlConflictPolicy Policy { get; } }
public sealed record PostgreSqlExecutionContext(Guid JobId, Guid RunId, long FenceToken, string ManifestHash);
public sealed record PostgreSqlTargetCheckpoint(Guid JobId, Guid RunId, long LastBatchSequence, byte[] LastStableKey, long CumulativeAffected, long CumulativeInserts, long CumulativeUpdates, string ManifestHash, long FenceToken);
public sealed record PostgreSqlResumePoint(long NextBatchSequence, StableKey? AfterStableKey);
public sealed record PostgreSqlBatchCommit(long Sequence, long Affected, long Inserts, long Updates);
public interface IDerivedCheckpointMirror { Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken); }
public interface IAfterTargetCommitBarrier { Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken); }
public sealed class PostgreSqlFenceLostException() : InvalidOperationException("The target checkpoint fence token no longer belongs to this worker.");
public sealed class PostgreSqlManifestMismatchException() : InvalidOperationException("The target checkpoint manifest hash differs from the sealed manifest.");
public sealed class PostgreSqlStrictExactBlockedException(string reason) : InvalidOperationException(reason);
public static class PostgreSqlStableKeyCodec
{
    public static byte[] Encode(StableKey key, PostgreSqlWriteTable table) { var buffer = new ArrayBufferWriter<byte>(); foreach (var column in table.StableKeyColumns) { var value = key.Components.Single(x => x.Column == column.Name).Value; if (value is null) throw new ArgumentException("Stable-key values cannot be null."); Write(buffer, value, column.ProviderType); } return buffer.WrittenSpan.ToArray(); }
    public static StableKey Decode(byte[] bytes, PostgreSqlWriteTable table) { var offset = 0; var parts = new List<KeyComponent>(); foreach (var column in table.StableKeyColumns) parts.Add(new(column.Name, Read(bytes, ref offset, column.ProviderType))); if (offset != bytes.Length) throw new ArgumentException("Stable-key encoding has trailing bytes."); return new StableKey(parts); }
    private static void Write(ArrayBufferWriter<byte> buffer, object value, NpgsqlDbType type) { var span = buffer.GetSpan(type is NpgsqlDbType.Integer ? 4 : 8); if (type == NpgsqlDbType.Integer && value is int integer) { BinaryPrimitives.WriteInt32BigEndian(span, integer); buffer.Advance(4); return; } if (type == NpgsqlDbType.Bigint && value is long bigInteger) { BinaryPrimitives.WriteInt64BigEndian(span, bigInteger); buffer.Advance(8); return; } var text = type == NpgsqlDbType.Text && value is string stringValue ? Encoding.UTF8.GetBytes(stringValue) : throw new NotSupportedException($"Stable-key type {type} is not supported."); BinaryPrimitives.WriteInt32BigEndian(span, text.Length); buffer.Advance(4); buffer.Write(text); }
    private static object Read(byte[] bytes, ref int offset, NpgsqlDbType type) { var span = bytes.AsSpan(offset); if (type == NpgsqlDbType.Integer) { offset += 4; return BinaryPrimitives.ReadInt32BigEndian(span); } if (type == NpgsqlDbType.Bigint) { offset += 8; return BinaryPrimitives.ReadInt64BigEndian(span); } if (type == NpgsqlDbType.Text) { var length = BinaryPrimitives.ReadInt32BigEndian(span); offset += 4; var value = Encoding.UTF8.GetString(bytes, offset, length); offset += length; return value; } throw new NotSupportedException($"Stable-key type {type} is not supported."); }
}

using DataPitcher.Core.Plans; using Npgsql; using NpgsqlTypes;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlTransferSchemaReader(NpgsqlDataSource dataSource)
{
    public async Task<PostgreSqlWriteTable> ReadAsync(string schema, string table, IReadOnlyCollection<string> stableKeys, CancellationToken cancellationToken)
    {
        const string sql = "SELECT a.attname,format_type(a.atttypid,a.atttypmod),t.typname,a.attgenerated::text,a.attidentity::text,co.collname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid JOIN pg_type t ON t.oid=a.atttypid LEFT JOIN pg_collation co ON co.oid=a.attcollation WHERE n.nspname=@schema AND c.relname=@table AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum";
        await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var columns = new List<PostgreSqlWriteColumn>();
        while (await reader.ReadAsync(cancellationToken)) { var type = Map(reader.GetString(2)); var name = reader.GetString(0); columns.Add(new(name, reader.GetString(1), type, stableKeys.Contains(name, StringComparer.Ordinal), reader.GetString(3) == "s", false, reader.GetString(4) == "a", reader.IsDBNull(5) ? null : reader.GetString(5))); }
        return new PostgreSqlWriteTable(new TableAddress(schema, table), columns);
    }
    private static NpgsqlDbType Map(string type) => type switch { "int4" => NpgsqlDbType.Integer, "int8" => NpgsqlDbType.Bigint, "text" => NpgsqlDbType.Text, "uuid" => NpgsqlDbType.Uuid, _ => throw new NotSupportedException($"PostgreSQL transfer column type '{type}' is not supported.") };
}

using DataPitcher.Core.Identity; using DataPitcher.Core.Plans; using DataPitcher.Providers.PostgreSql; using NpgsqlTypes;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
internal static class PostgreSqlTransferTestData
{
    public static PostgreSqlWriteTable Table(string schema) => new(new(schema, "transfer_rows"), [new("id", "integer", NpgsqlDbType.Integer, true, false, false, false, null), new("code", "text", NpgsqlDbType.Text, false, false, false, false, "C"), new("computed", "integer", NpgsqlDbType.Integer, false, true, false, false, null)]);
    public static PostgreSqlWriteTable TextKeyTable(string schema) => new(new(schema, "transfer_rows"), [new("code", "text", NpgsqlDbType.Text, true, false, false, false, "C")]);
    public static PostgreSqlTransferBatch Batch(long sequence, params (int Id, string Code)[] rows) => new(sequence, rows.Select(row => new PostgreSqlTransferRow(new StableKey([new KeyComponent("id", row.Id)]), new Dictionary<string, object?> { ["id"] = row.Id, ["code"] = row.Code })), new StableKey([new KeyComponent("id", rows.Last().Id)]), PostgreSqlConflictPolicy.InsertOnly);
    public static PostgreSqlExecutionContext Context(long fence = 1) => new(Guid.NewGuid(), Guid.NewGuid(), fence, "sealed-manifest-hash");
}
```

The separate reader fails unknown types before COPY. Insert omits generated/row-version columns; update also omits stable and identity-always columns. Keys are raw bytes, never culture-formatted text.

- [ ] **Step 4: Run the focused test and confirm every public contract passes.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTransferModelsTests"`

Expected: `Passed: 2. Failed: 0.`

- [ ] **Step 5: Commit the contracts and catalog reader.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferModels.cs src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferSchemaReader.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferTestData.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferModelsTests.cs && git commit -m "feat: define postgres transfer contracts"`

### Task 2: Persist and fence the authoritative target checkpoint

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlTargetCheckpointStore.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTargetCheckpointStoreTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTargetCheckpointStoreTests.cs`

- [ ] **Step 1: Write the failing target-checkpoint tests.**

```csharp
using DataPitcher.Providers.PostgreSql; using Xunit;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
public sealed class PostgreSqlTargetCheckpointStoreTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;
    public PostgreSqlTargetCheckpointStoreTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task AdvanceAsync_WritesBatchAndFenceOnlyWhenTheTargetTokenMatches()
    {
        await using var scope = await _fixture.CreateScopeAsync(); var store = new PostgreSqlTargetCheckpointStore(scope.Target); var context = PostgreSqlTransferTestData.Context();
        await store.InitializeAsync(context, CancellationToken.None);
        await using var connection = await scope.Target.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await store.AdvanceAsync(connection, transaction, context, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "a")), 1, 1, 0, CancellationToken.None); await transaction.CommitAsync();
        var checkpoint = Assert.NotNull(await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None));
        Assert.Equal(0, checkpoint.LastBatchSequence); Assert.Equal(1, checkpoint.CumulativeAffected); Assert.Equal(1, checkpoint.FenceToken);
    }
    [Fact]
    public async Task AdvanceAsync_WhenNewerWorkerOwnsTheFence_ThrowsWithoutAdvancing()
    {
        await using var scope = await _fixture.CreateScopeAsync(); var store = new PostgreSqlTargetCheckpointStore(scope.Target); var stale = PostgreSqlTransferTestData.Context(); var current = stale with { FenceToken = 2 };
        await store.InitializeAsync(stale, CancellationToken.None); await store.InitializeAsync(current, CancellationToken.None);
        await using var connection = await scope.Target.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<PostgreSqlFenceLostException>(() => store.AdvanceAsync(connection, transaction, stale, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "a")), 1, 1, 0, CancellationToken.None));
        await transaction.RollbackAsync(); Assert.Equal(2, (await store.ReadAsync(stale.JobId, stale.RunId, CancellationToken.None))!.FenceToken);
    }
}
```

- [ ] **Step 2: Run the checkpoint tests and confirm the store is absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTargetCheckpointStoreTests"`

Expected: compilation fails with CS0246: `PostgreSqlTargetCheckpointStore` could not be found.

- [ ] **Step 3: Implement the target checkpoint store and exact conditional update.**

```csharp
using Npgsql;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlTargetCheckpointStore(NpgsqlDataSource dataSource)
{
    private const string Name = "datapitcher.transfer_checkpoints";
    public async Task InitializeAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS " + Name + " (job_id uuid NOT NULL, run_id uuid NOT NULL, last_batch_sequence bigint NOT NULL, last_stable_key bytea NOT NULL, cumulative_affected bigint NOT NULL, cumulative_inserts bigint NOT NULL, cumulative_updates bigint NOT NULL, manifest_hash text NOT NULL, fence_token bigint NOT NULL, PRIMARY KEY (job_id, run_id))", cancellationToken);
        var existing = await ReadAsync(connection, transaction, context.JobId, context.RunId, cancellationToken);
        if (existing is null) { await ExecuteAsync(connection, transaction, "INSERT INTO " + Name + " VALUES (@job,@run,-1,''::bytea,0,0,0,@hash,@fence)", cancellationToken, context); }
        else if (!StringComparer.Ordinal.Equals(existing.ManifestHash, context.ManifestHash)) throw new PostgreSqlManifestMismatchException();
        else if (existing.FenceToken > context.FenceToken) throw new PostgreSqlFenceLostException();
        else if (existing.FenceToken < context.FenceToken && await ExecuteAsync(connection, transaction, "UPDATE " + Name + " SET fence_token=@fence WHERE job_id=@job AND run_id=@run AND fence_token < @fence", cancellationToken, context) != 1) throw new PostgreSqlFenceLostException();
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<PostgreSqlTargetCheckpoint?> ReadAsync(Guid jobId, Guid runId, CancellationToken cancellationToken) { await using var connection = await dataSource.OpenConnectionAsync(cancellationToken); return await ReadAsync(connection, null, jobId, runId, cancellationToken); }
    public async Task AdvanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlExecutionContext context, PostgreSqlWriteTable table, PostgreSqlTransferBatch batch, long affected, long inserts, long updates, CancellationToken cancellationToken)
    {
        var key = PostgreSqlStableKeyCodec.Encode(batch.LastStableKey, table); await using var command = new NpgsqlCommand("UPDATE " + Name + " SET last_batch_sequence=@sequence,last_stable_key=@key,cumulative_affected=cumulative_affected+@affected,cumulative_inserts=cumulative_inserts+@inserts,cumulative_updates=cumulative_updates+@updates WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token=@fence AND last_batch_sequence=@previous", connection, transaction);
        command.Parameters.AddWithValue("sequence", batch.Sequence); command.Parameters.AddWithValue("key", key); command.Parameters.AddWithValue("affected", affected); command.Parameters.AddWithValue("inserts", inserts); command.Parameters.AddWithValue("updates", updates); AddContext(command, context); command.Parameters.AddWithValue("previous", batch.Sequence - 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new PostgreSqlFenceLostException();
    }
    private static async Task<PostgreSqlTargetCheckpoint?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid job, Guid run, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand("SELECT job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token FROM " + Name + " WHERE job_id=@job AND run_id=@run", connection, transaction); command.Parameters.AddWithValue("job", job); command.Parameters.AddWithValue("run", run); await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8)) : null; }
    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, PostgreSqlExecutionContext? context = null) { await using var command = new NpgsqlCommand(sql, connection, transaction); if (context is not null) AddContext(command, context); return await command.ExecuteNonQueryAsync(cancellationToken); }
    private static void AddContext(NpgsqlCommand command, PostgreSqlExecutionContext context) { command.Parameters.AddWithValue("job", context.JobId); command.Parameters.AddWithValue("run", context.RunId); command.Parameters.AddWithValue("hash", context.ManifestHash); command.Parameters.AddWithValue("fence", context.FenceToken); }
}
```

Sequence `-1` and an empty key mean no committed batch. Initialization advances only lower target tokens. `AdvanceAsync` checks manifest, token, and prior sequence; zero rows aborts the caller transaction.

- [ ] **Step 4: Run the checkpoint tests and confirm target fencing passes.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTargetCheckpointStoreTests"`

Expected: `Passed: 2. Failed: 0.` The stale conditional update affects zero rows.

- [ ] **Step 5: Commit the authoritative checkpoint store.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlTargetCheckpointStore.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTargetCheckpointStoreTests.cs && git commit -m "feat: fence postgres target checkpoints"`

### Task 3: Stage with direct binary COPY and apply each conflict policy

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlBatchStageWriter.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlBatchApplier.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlBatchExecutionTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlBatchExecutionTests.cs`

- [ ] **Step 1: Write the failing native-writer and affected-key tests.**

```csharp
using DataPitcher.Providers.PostgreSql; using Xunit;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
public sealed class PostgreSqlBatchExecutionTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;
    public PostgreSqlBatchExecutionTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task StageAsync_WhenSecondRowFailsBeforeComplete_AbortsCopyAndRecordsNoCheckpoint()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context(); var table = PostgreSqlTransferTestData.Table(scope.Schema); var checkpoints = new PostgreSqlTargetCheckpointStore(scope.Target); await checkpoints.InitializeAsync(context, CancellationToken.None);
        var bad = new PostgreSqlTransferBatch(0, [new(new DataPitcher.Core.Identity.StableKey([new("id", 1)]), new Dictionary<string, object?> { ["id"] = 1, ["code"] = "ok" }), new(new DataPitcher.Core.Identity.StableKey([new("id", 2)]), new Dictionary<string, object?> { ["id"] = "not-an-integer", ["code"] = "bad" })], new([new("id", 2)]), PostgreSqlConflictPolicy.InsertOnly);
        await using var connection = await scope.Target.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => new PostgreSqlBatchStageWriter().StageAsync(connection, transaction, context, table, bad, CancellationToken.None)); await transaction.RollbackAsync();
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows")); Assert.Equal(-1, (await checkpoints.ReadAsync(context.JobId, context.RunId, CancellationToken.None))!.LastBatchSequence);
    }
    [Theory]
    [InlineData(PostgreSqlConflictPolicy.InsertOnly, 2, 0, 2)] [InlineData(PostgreSqlConflictPolicy.SkipExisting, 1, 0, 1)] [InlineData(PostgreSqlConflictPolicy.Upsert, 2, 1, 1)]
    public async Task ApplyAsync_CapturesOnlyInsertedOrUpdatedKeysAfterTheCallerCommits(PostgreSqlConflictPolicy policy, int affected, int updates, int inserts)
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);"); if (policy != PostgreSqlConflictPolicy.InsertOnly) await scope.ExecuteTargetAsync("INSERT INTO transfer_rows VALUES (1,'old');");
        var table = PostgreSqlTransferTestData.Table(scope.Schema); var context = PostgreSqlTransferTestData.Context(); var batch = PostgreSqlTransferTestData.Batch(0, (1, "new"), (2, "two")); batch = new(batch.Sequence, batch.Rows, batch.LastStableKey, policy);
        await using var connection = await scope.Target.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync(); var writer = new PostgreSqlBatchStageWriter(); await writer.StageAsync(connection, transaction, context, table, batch, CancellationToken.None);
        var result = await new PostgreSqlBatchApplier().ApplyAsync(connection, transaction, context, table, batch, CancellationToken.None); Assert.Equal(affected, result.Affected); Assert.Equal(updates, result.Updates); Assert.Equal(inserts, result.Inserts); await transaction.CommitAsync();
        Assert.Equal((long)affected, await scope.ScalarTargetAsync<long>($"SELECT count(*) FROM datapitcher.transfer_affected_keys WHERE job_id='{context.JobId}'"));
    }
}
```

- [ ] **Step 2: Run the writer tests and confirm the writer and applier are absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlBatchExecutionTests"`

Expected: compilation fails with CS0246: `PostgreSqlBatchStageWriter` could not be found.

- [ ] **Step 3: Implement stage COPY, separate DML, and capture persistence.**

```csharp
using Npgsql; using NpgsqlTypes;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlBatchStageWriter
{
    public async Task StageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlExecutionContext context, PostgreSqlWriteTable table, PostgreSqlTransferBatch batch, CancellationToken cancellationToken)
    {
        var stage = StageName(table); var columns = table.InsertColumns; var names = string.Join(", ", new[] { "job_id", "run_id", "fence_token", "batch_sequence" }.Concat(columns.Select(x => PostgreSqlIdentifier.Quote(x.Name))));
        var declaration = string.Join(", ", new[] { "job_id uuid NOT NULL", "run_id uuid NOT NULL", "fence_token bigint NOT NULL", "batch_sequence bigint NOT NULL" }.Concat(columns.Select(x => PostgreSqlIdentifier.Quote(x.Name) + " " + x.StoreType)));
        await ExecuteAsync(connection, transaction, "CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS " + stage + " (" + declaration + ")", cancellationToken);
        await using var importer = await connection.BeginBinaryImportAsync("COPY " + stage + " (" + names + ") FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var row in batch.Rows) { await importer.StartRowAsync(cancellationToken); await importer.WriteAsync(context.JobId, NpgsqlDbType.Uuid, cancellationToken); await importer.WriteAsync(context.RunId, NpgsqlDbType.Uuid, cancellationToken); await importer.WriteAsync(context.FenceToken, NpgsqlDbType.Bigint, cancellationToken); await importer.WriteAsync(batch.Sequence, NpgsqlDbType.Bigint, cancellationToken); foreach (var column in columns) { var value = row.Values[column.Name]; if (value is null) await importer.WriteNullAsync(cancellationToken); else await importer.WriteAsync(value, column.ProviderType, cancellationToken); } }
        await importer.CompleteAsync(cancellationToken);
    }
    public static string StageName(PostgreSqlWriteTable table) => PostgreSqlIdentifier.Qualified("datapitcher", "stage_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(table.Target.Schema + "\u001f" + table.Target.Name))).ToLowerInvariant());
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand(sql, connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
}

using DataPitcher.Core.Identity; using Npgsql;
namespace DataPitcher.Providers.PostgreSql;
public sealed record PostgreSqlApplyResult(long Affected, long Inserts, long Updates);
public sealed class PostgreSqlBatchApplier
{
    public async Task<PostgreSqlApplyResult> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlExecutionContext context, PostgreSqlWriteTable table, PostgreSqlTransferBatch batch, CancellationToken cancellationToken)
    {
        await EnsureLedgerAsync(connection, transaction, cancellationToken); var affected = new List<StableKey>(); var updates = batch.Policy == PostgreSqlConflictPolicy.Upsert ? await ExecuteReturningAsync(connection, transaction, UpdateSql(table), context, batch.Sequence, table, cancellationToken) : [];
        affected.AddRange(updates); var inserts = await ExecuteReturningAsync(connection, transaction, InsertSql(table, batch.Policy), context, batch.Sequence, table, cancellationToken); affected.AddRange(inserts);
        foreach (var key in affected) await RecordAsync(connection, transaction, context, table, key, cancellationToken);
        return new(affected.Count, inserts.Count, updates.Count);
    }
    private static string InsertSql(PostgreSqlWriteTable table, PostgreSqlConflictPolicy policy) { var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name); var stage = PostgreSqlBatchStageWriter.StageName(table); var columns = string.Join(", ", table.InsertColumns.Select(x => PostgreSqlIdentifier.Quote(x.Name))); var keys = Join(table.StableKeyColumns, "s", "t"); var missing = policy == PostgreSqlConflictPolicy.InsertOnly ? "" : " AND NOT EXISTS (SELECT 1 FROM " + target + " t WHERE " + keys + ")"; var overriding = table.InsertColumns.Any(x => x.IsIdentityAlways) ? " OVERRIDING SYSTEM VALUE" : ""; return "INSERT INTO " + target + " (" + columns + ")" + overriding + " SELECT " + columns + " FROM " + stage + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence" + missing + " RETURNING " + string.Join(", ", table.StableKeyColumns.Select(x => PostgreSqlIdentifier.Quote(x.Name))); }
    private static string UpdateSql(PostgreSqlWriteTable table) { var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name); var stage = PostgreSqlBatchStageWriter.StageName(table); var set = string.Join(", ", table.UpdateColumns.Select(x => PostgreSqlIdentifier.Quote(x.Name) + "=s." + PostgreSqlIdentifier.Quote(x.Name))); return "UPDATE " + target + " t SET " + set + " FROM " + stage + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence AND " + Join(table.StableKeyColumns, "s", "t") + " RETURNING " + string.Join(", ", table.StableKeyColumns.Select(x => "t." + PostgreSqlIdentifier.Quote(x.Name))); }
    private static string Join(IEnumerable<PostgreSqlWriteColumn> columns, string left, string right) => string.Join(" AND ", columns.Select(x => left + "." + PostgreSqlIdentifier.Quote(x.Name) + "=" + right + "." + PostgreSqlIdentifier.Quote(x.Name)));
    private static async Task<List<StableKey>> ExecuteReturningAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, PostgreSqlExecutionContext context, long sequence, PostgreSqlWriteTable table, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("job", context.JobId); command.Parameters.AddWithValue("run", context.RunId); command.Parameters.AddWithValue("fence", context.FenceToken); command.Parameters.AddWithValue("sequence", sequence); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var keys = new List<StableKey>(); while (await reader.ReadAsync(cancellationToken)) keys.Add(new StableKey(table.StableKeyColumns.Select((column, index) => new KeyComponent(column.Name, reader.GetValue(index))))); return keys; }
    private static async Task EnsureLedgerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS datapitcher.transfer_affected_keys (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key)); CREATE TABLE IF NOT EXISTS datapitcher.transfer_write_manifest (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key));", connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlExecutionContext context, PostgreSqlWriteTable table, StableKey key, CancellationToken cancellationToken) { await using var command = new NpgsqlCommand("INSERT INTO datapitcher.transfer_affected_keys VALUES (@job,@run,@schema,@table,@key) ON CONFLICT DO NOTHING", connection, transaction); command.Parameters.AddWithValue("job", context.JobId); command.Parameters.AddWithValue("run", context.RunId); command.Parameters.AddWithValue("schema", table.Target.Schema); command.Parameters.AddWithValue("table", table.Target.Name); command.Parameters.AddWithValue("key", PostgreSqlStableKeyCodec.Encode(key, table)); await command.ExecuteNonQueryAsync(cancellationToken); }
}
```

Only `CompleteAsync` completes COPY; earlier disposal aborts it. Stage, DML, effects, and checkpoint share one transaction. There is no MERGE: InsertOnly throws conflicts, SkipExisting inserts misses, Upsert captures UPDATE then INSERT. Captures are evidence only after commit.

- [ ] **Step 4: Run the native writer tests and confirm COPY abort and policies pass.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlBatchExecutionTests"`

Expected: `Passed: 4. Failed: 0.`

- [ ] **Step 5: Commit the direct COPY writer and applier.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlBatchStageWriter.cs src/DataPitcher.Providers.PostgreSql/PostgreSqlBatchApplier.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlBatchExecutionTests.cs && git commit -m "feat: apply postgres transfer batches"`

### Task 4: Recover from target state and seek the next source batch

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferExecutor.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlKeysetSeek.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferRecoveryTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferRecoveryTests.cs`

- [ ] **Step 1: Write the failing deterministic crash, stale-worker, and keyset tests.**

```csharp
using DataPitcher.Providers.PostgreSql; using Xunit;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
public sealed class PostgreSqlTransferRecoveryTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;
    public PostgreSqlTransferRecoveryTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task ExecuteAsync_WhenProcessDiesAfterTargetCommit_RecoversIfAndOnlyIfTheCheckpointAdvanced()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var context = PostgreSqlTransferTestData.Context(); var mirror = new RecordingMirror(); var barrier = new CrashBarrier(); var executor = new PostgreSqlTransferExecutor(scope.Target, mirror, barrier);
        var running = executor.ExecuteAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        await barrier.Reached.Task; Assert.Equal(1L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows")); Assert.Equal(0, mirror.Writes);
        barrier.Crash.SetResult(true); await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        var resume = await executor.RecoverAsync(context, PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None);
        Assert.Equal(1, resume.NextBatchSequence); Assert.Equal(1, resume.AfterStableKey!.Components.Single().Value); Assert.Equal(1, mirror.Writes);
    }
    [Fact]
    public async Task ExecuteAsync_WhenWorkerFenceIsStale_RollsBackBusinessRowsDeterministically()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var stale = PostgreSqlTransferTestData.Context(); var current = stale with { FenceToken = 2 }; var executor = new PostgreSqlTransferExecutor(scope.Target, new RecordingMirror(), new PassBarrier()); await executor.InitializeAsync(stale, CancellationToken.None); await executor.InitializeAsync(current, CancellationToken.None);
        await Assert.ThrowsAsync<PostgreSqlFenceLostException>(() => executor.ExecuteAsync(stale, PostgreSqlTransferTestData.Table(scope.Schema), PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None));
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM transfer_rows"));
    }
    [Fact]
    public void Build_UsesCompositeKeysetSeekingAndCOrdinalTextWithoutOffset()
    {
        var seek = PostgreSqlKeysetSeek.Build(PostgreSqlTransferTestData.TextKeyTable("dp"), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 100);
        Assert.Contains("WHERE (s.\"code\" COLLATE \"C\">@k0)", seek.Sql, StringComparison.Ordinal); Assert.Contains("LIMIT @limit", seek.Sql, StringComparison.Ordinal); Assert.DoesNotContain("OFFSET", seek.Sql, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class RecordingMirror : IDerivedCheckpointMirror { public int Writes { get; private set; } public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) { Writes++; return Task.CompletedTask; } }
    private sealed class CrashBarrier : IAfterTargetCommitBarrier { public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource<bool> Crash { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public async Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) { Reached.SetResult(true); await Crash.Task; throw new InvalidOperationException("simulated process death"); } }
    private sealed class PassBarrier : IAfterTargetCommitBarrier { public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask; }
}
```

- [ ] **Step 2: Run the recovery tests and confirm execution and seek types are absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTransferRecoveryTests"`

Expected: compilation fails with CS0246: `PostgreSqlTransferExecutor` and `PostgreSqlKeysetSeek` could not be found.

- [ ] **Step 3: Implement the atomic batch executor, target-only recovery, and keyset SQL.**

```csharp
using Npgsql;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlTransferExecutor
{
    private readonly NpgsqlDataSource _target; private readonly IDerivedCheckpointMirror _mirror; private readonly IAfterTargetCommitBarrier _barrier; private readonly PostgreSqlTargetCheckpointStore _checkpoints;
    public PostgreSqlTransferExecutor(NpgsqlDataSource target, IDerivedCheckpointMirror mirror, IAfterTargetCommitBarrier barrier) { _target = target; _mirror = mirror; _barrier = barrier; _checkpoints = new PostgreSqlTargetCheckpointStore(target); }
    public Task InitializeAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken) => _checkpoints.InitializeAsync(context, cancellationToken);
    public async Task<PostgreSqlBatchCommit> ExecuteAsync(PostgreSqlExecutionContext context, PostgreSqlWriteTable table, PostgreSqlTransferBatch batch, CancellationToken cancellationToken)
    {
        await InitializeAsync(context, cancellationToken); await using var connection = await _target.OpenConnectionAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await new PostgreSqlBatchStageWriter().StageAsync(connection, transaction, context, table, batch, cancellationToken); var result = await new PostgreSqlBatchApplier().ApplyAsync(connection, transaction, context, table, batch, cancellationToken);
        await _checkpoints.AdvanceAsync(connection, transaction, context, table, batch, result.Affected, result.Inserts, result.Updates, cancellationToken); await transaction.CommitAsync(cancellationToken);
        var checkpoint = (await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken))!; await _barrier.WaitAsync(checkpoint, cancellationToken); await _mirror.WriteAsync(checkpoint, cancellationToken); return new(batch.Sequence, result.Affected, result.Inserts, result.Updates);
    }
    public async Task<PostgreSqlResumePoint> RecoverAsync(PostgreSqlExecutionContext context, PostgreSqlWriteTable table, CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken) ?? throw new InvalidOperationException("Target checkpoint was not initialized."); if (!StringComparer.Ordinal.Equals(checkpoint.ManifestHash, context.ManifestHash)) throw new PostgreSqlManifestMismatchException();
        await _mirror.WriteAsync(checkpoint, cancellationToken); return new(checkpoint.LastBatchSequence + 1, checkpoint.LastBatchSequence < 0 ? null : PostgreSqlStableKeyCodec.Decode(checkpoint.LastStableKey, table));
    }
}

using DataPitcher.Core.Identity; using Npgsql;
namespace DataPitcher.Providers.PostgreSql;
public sealed record PostgreSqlSeekQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);
public static class PostgreSqlKeysetSeek
{
    public static PostgreSqlSeekQuery Build(PostgreSqlWriteTable table, StableKey after, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit)); var columns = table.StableKeyColumns; var predicates = new List<string>(); var parameters = new List<NpgsqlParameter>();
        for (var index = 0; index < columns.Count; index++) { var equal = string.Join(" AND ", Enumerable.Range(0, index).Select(i => Expression(columns[i]) + "=@k" + i)); predicates.Add((equal.Length == 0 ? "" : equal + " AND ") + Expression(columns[index]) + ">@k" + index); }
        for (var index = 0; index < columns.Count; index++) parameters.Add(new NpgsqlParameter("k" + index, columns[index].ProviderType) { Value = after.Components.Single(x => x.Column == columns[index].Name).Value! }); parameters.Add(new NpgsqlParameter("limit", limit));
        var order = string.Join(", ", columns.Select(Expression)); var select = string.Join(", ", table.InsertColumns.Select(column => "s." + PostgreSqlIdentifier.Quote(column.Name))); return new("SELECT " + select + " FROM " + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name) + " s WHERE (" + string.Join(" OR ", predicates) + ") ORDER BY " + order + " LIMIT @limit", Array.AsReadOnly(parameters.ToArray()));
    }
    private static string Expression(PostgreSqlWriteColumn column) => "s." + PostgreSqlIdentifier.Quote(column.Name) + (column.ProviderType == NpgsqlTypes.NpgsqlDbType.Text ? " COLLATE \"C\"" : "");
}
```

The barrier is deterministic: the test observes the commit before releasing its crash. The mirror is write-only and post-barrier. The pipeline receives lexicographic keyset SQL, never OFFSET. Text stable keys require matching database `C` and manifest ordinal ordering; otherwise sealing blocks them.

- [ ] **Step 4: Run recovery tests and confirm the commit gap and fence cases pass.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlTransferRecoveryTests"`

Expected: `Passed: 3. Failed: 0.`

- [ ] **Step 5: Commit target-based recovery and keyset seeking.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlTransferExecutor.cs src/DataPitcher.Providers.PostgreSql/PostgreSqlKeysetSeek.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlTransferRecoveryTests.cs && git commit -m "feat: recover postgres transfer batches"`

### Task 5: Block unsafe StrictExact plans, verify committed keys, and realign sequences

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlStrictExact.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStrictExactTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStrictExactTests.cs`

- [ ] **Step 1: Write the failing StrictExact and sequence tests.**

```csharp
using DataPitcher.Core.Identity; using DataPitcher.Providers.PostgreSql; using Xunit;
namespace DataPitcher.Providers.PostgreSql.IntegrationTests;
public sealed class PostgreSqlStrictExactTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;
    public PostgreSqlStrictExactTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;
    [Fact]
    public async Task EnsureAvailableAsync_WhenTargetHasUserTrigger_RefusesStrictExactWithoutDowngrade()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL); CREATE FUNCTION transfer_notice() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$; CREATE TRIGGER transfer_trigger BEFORE INSERT ON transfer_rows FOR EACH ROW EXECUTE FUNCTION transfer_notice();");
        var error = await Assert.ThrowsAsync<PostgreSqlStrictExactBlockedException>(() => new PostgreSqlStrictExact(scope.Target).EnsureAvailableAsync(PostgreSqlTransferTestData.Table(scope.Schema), CancellationToken.None));
        Assert.Contains("trigger", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task VerifyAsync_AfterCommit_EqualsPlannedBusinessKeysAndExcludesDatapitcherObjects()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE transfer_rows (id integer PRIMARY KEY, code text NOT NULL);");
        var strict = new PostgreSqlStrictExact(scope.Target); var context = PostgreSqlTransferTestData.Context(); var table = PostgreSqlTransferTestData.Table(scope.Schema); var key = new StableKey([new KeyComponent("id", 1)]);
        await strict.RecordPlannedAsync(context, table, [key], CancellationToken.None); await new PostgreSqlTransferExecutor(scope.Target, new Mirror(), new Barrier()).ExecuteAsync(context, table, PostgreSqlTransferTestData.Batch(0, (1, "one")), CancellationToken.None);
        await strict.VerifyAsync(context, CancellationToken.None); await strict.RecordPlannedAsync(context, table, [new StableKey([new KeyComponent("id", 2)])], CancellationToken.None); await Assert.ThrowsAsync<InvalidOperationException>(() => strict.VerifyAsync(context, CancellationToken.None));
    }
    [Fact]
    public async Task RealignAsync_UsesOwnedSequenceDirectionAndNeverRewindsOrRollsBackSetval()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE sequence_rows (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, code text NOT NULL); INSERT INTO sequence_rows (id,code) OVERRIDING SYSTEM VALUE VALUES (10,'ten');");
        var table = new PostgreSqlWriteTable(new(scope.Schema, "sequence_rows"), [new("id", "bigint", NpgsqlTypes.NpgsqlDbType.Bigint, true, false, false, true, null), new("code", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C")]); var realigner = new PostgreSqlSequenceRealigner(scope.Target);
        await realigner.RealignAsync(table, "id", CancellationToken.None); Assert.Equal(11L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('next') RETURNING id"));
        await using (var connection = await scope.Target.OpenConnectionAsync()) { await using var transaction = await connection.BeginTransactionAsync(); await using var set = new Npgsql.NpgsqlCommand("SELECT setval(pg_get_serial_sequence('sequence_rows','id'),50,true)", connection, transaction); await set.ExecuteNonQueryAsync(); await transaction.RollbackAsync(); }
        await realigner.RealignAsync(table, "id", CancellationToken.None); Assert.Equal(51L, await scope.ScalarTargetAsync<long>("INSERT INTO sequence_rows (code) VALUES ('ahead') RETURNING id"));
    }
    [Fact]
    public async Task RealignAsync_WhenSequenceDecreases_UsesTheOccupiedMinimum()
    {
        await using var scope = await _fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE SEQUENCE descending_rows_id_seq INCREMENT BY -1 START WITH -1; CREATE TABLE descending_rows (id bigint PRIMARY KEY DEFAULT nextval('descending_rows_id_seq'), code text NOT NULL); ALTER SEQUENCE descending_rows_id_seq OWNED BY descending_rows.id; INSERT INTO descending_rows (id,code) VALUES (-10,'ten');");
        var table = new PostgreSqlWriteTable(new(scope.Schema, "descending_rows"), [new("id", "bigint", NpgsqlTypes.NpgsqlDbType.Bigint, true, false, false, false, null), new("code", "text", NpgsqlTypes.NpgsqlDbType.Text, false, false, false, false, "C")]); await new PostgreSqlSequenceRealigner(scope.Target).RealignAsync(table, "id", CancellationToken.None);
        Assert.Equal(-11L, await scope.ScalarTargetAsync<long>("INSERT INTO descending_rows (code) VALUES ('next') RETURNING id"));
    }
    private sealed class Mirror : IDerivedCheckpointMirror { public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class Barrier : IAfterTargetCommitBarrier { public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) => Task.CompletedTask; }
}
```

- [ ] **Step 2: Run the StrictExact tests and confirm the verifier is absent.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlStrictExactTests"`

Expected: compilation fails with CS0246: `PostgreSqlStrictExact` and `PostgreSqlSequenceRealigner` could not be found.

- [ ] **Step 3: Implement bounded side-effect inspection, committed-key equality, and catalog-owned sequence adjustment.**

```csharp
using DataPitcher.Core.Identity; using Npgsql;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlStrictExact(NpgsqlDataSource dataSource)
{
    public async Task EnsureAvailableAsync(PostgreSqlWriteTable table, CancellationToken cancellationToken)
    {
        var target = PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name); var trigger = await ExistsAsync("SELECT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid=@target::regclass AND NOT t.tgisinternal AND t.tgenabled <> 'D')", target, cancellationToken); var rule = await ExistsAsync("SELECT EXISTS (SELECT 1 FROM pg_rewrite r WHERE r.ev_class=@target::regclass AND r.rulename <> '_RETURN')", target, cancellationToken); var cascade = await ExistsAsync("SELECT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conrelid=@target::regclass AND c.contype='f' AND (c.confupdtype IN ('c','n','d') OR c.confdeltype IN ('c','n','d')))", target, cancellationToken);
        if (trigger || rule || cascade) throw new PostgreSqlStrictExactBlockedException(trigger ? "StrictExact is blocked by a target trigger." : rule ? "StrictExact is blocked by a target rewrite rule." : "StrictExact is blocked by a target cascading write path.");
    }
    public async Task RecordPlannedAsync(PostgreSqlExecutionContext context, PostgreSqlWriteTable table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken); await using var create = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS datapitcher; CREATE TABLE IF NOT EXISTS datapitcher.transfer_write_manifest (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key)); CREATE TABLE IF NOT EXISTS datapitcher.transfer_affected_keys (job_id uuid NOT NULL,run_id uuid NOT NULL,table_schema text NOT NULL,table_name text NOT NULL,stable_key bytea NOT NULL,PRIMARY KEY(job_id,run_id,table_schema,table_name,stable_key));", connection, transaction); await create.ExecuteNonQueryAsync(cancellationToken);
        foreach (var key in keys) { await using var insert = new NpgsqlCommand("INSERT INTO datapitcher.transfer_write_manifest VALUES (@job,@run,@schema,@table,@key) ON CONFLICT DO NOTHING", connection, transaction); insert.Parameters.AddWithValue("job", context.JobId); insert.Parameters.AddWithValue("run", context.RunId); insert.Parameters.AddWithValue("schema", table.Target.Schema); insert.Parameters.AddWithValue("table", table.Target.Name); insert.Parameters.AddWithValue("key", PostgreSqlStableKeyCodec.Encode(key, table)); await insert.ExecuteNonQueryAsync(cancellationToken); } await transaction.CommitAsync(cancellationToken);
    }
    public async Task VerifyAsync(PostgreSqlExecutionContext context, CancellationToken cancellationToken)
    {
        const string sql = "(SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_affected_keys WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_write_manifest WHERE job_id=@job AND run_id=@run) UNION ALL (SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_write_manifest WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM datapitcher.transfer_affected_keys WHERE job_id=@job AND run_id=@run)";
        await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("job", context.JobId); command.Parameters.AddWithValue("run", context.RunId); await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Committed affected keys differ from the planned write manifest.");
    }
    private async Task<bool> ExistsAsync(string sql, string target, CancellationToken cancellationToken) { await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("target", target); return (bool)(await command.ExecuteScalarAsync(cancellationToken))!; }
}
public sealed class PostgreSqlSequenceRealigner(NpgsqlDataSource dataSource)
{
    public async Task RealignAsync(PostgreSqlWriteTable table, string column, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken); await using var lockCommand = new NpgsqlCommand("LOCK TABLE " + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name) + " IN ACCESS EXCLUSIVE MODE", connection, transaction); await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        const string sequenceSql = "SELECT seq.oid::regclass::text,s.seqincrement,s.seqcycle,count(*) OVER() FROM pg_class tab JOIN pg_namespace ns ON ns.oid=tab.relnamespace JOIN pg_attribute att ON att.attrelid=tab.oid JOIN pg_depend dep ON dep.refobjid=tab.oid AND dep.refobjsubid=att.attnum AND dep.deptype IN ('a','i') JOIN pg_class seq ON seq.oid=dep.objid JOIN pg_sequence s ON s.seqrelid=seq.oid WHERE ns.nspname=@schema AND tab.relname=@table AND att.attname=@column";
        await using var sequence = new NpgsqlCommand(sequenceSql, connection, transaction); sequence.Parameters.AddWithValue("schema", table.Target.Schema); sequence.Parameters.AddWithValue("table", table.Target.Name); sequence.Parameters.AddWithValue("column", column); await using var reader = await sequence.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return; var name = reader.GetString(0); var increment = reader.GetInt64(1); if (reader.GetBoolean(2) || reader.GetInt64(3) != 1) throw new NotSupportedException("Shared or cycling sequences are not supported."); await reader.CloseAsync();
        await using var current = new NpgsqlCommand("SELECT last_value,is_called FROM " + name, connection, transaction); await using var currentReader = await current.ExecuteReaderAsync(cancellationToken); await currentReader.ReadAsync(cancellationToken); var last = currentReader.GetInt64(0); var called = currentReader.GetBoolean(1); await currentReader.CloseAsync();
        await using var extreme = new NpgsqlCommand("SELECT " + (increment > 0 ? "max" : "min") + "(" + PostgreSqlIdentifier.Quote(column) + ") FROM " + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name), connection, transaction); var value = await extreme.ExecuteScalarAsync(cancellationToken); if (value is null || value is DBNull) { await transaction.CommitAsync(cancellationToken); return; } var bound = Convert.ToInt64(value); var next = called ? checked(last + increment) : last; if ((increment > 0 && next > bound) || (increment < 0 && next < bound)) { await transaction.CommitAsync(cancellationToken); return; }
        await using var set = new NpgsqlCommand("SELECT setval(@sequence::regclass,@value,true)", connection, transaction); set.Parameters.AddWithValue("sequence", name); set.Parameters.AddWithValue("value", bound); await set.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
}
```

The preflight blocks rather than inferring trigger closure. Invoke it before sealing and again before first write. Ledgers contain only business addresses and encoded keys, never checkpoint or staging names; compare them only after all commits.

The realigner discovers ownership through `pg_depend`, rejects shared/cycling sequences, and locks the table while deployment excludes other sequence users. It moves a positive sequence to `max` or a negative one to `min` only when needed; `setval(..., true)` makes the next value one increment beyond it. `setval` is immediate and survives rollback.

- [ ] **Step 4: Run the StrictExact and generator tests and confirm they pass.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlStrictExactTests"`

Expected: `Passed: 4. Failed: 0.`

- [ ] **Step 5: Commit StrictExact verification and sequence safety.**

Run: `git add src/DataPitcher.Providers.PostgreSql/PostgreSqlStrictExact.cs tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlStrictExactTests.cs && git commit -m "feat: verify postgres transfer effects"`

### Task 6: Run the merged coverage gate

**Files:**
- Create: none
- Modify: none
- Test: `scripts/test-all.sh`

- [ ] **Step 1: Run the complete PostgreSQL integration assembly.**

Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj`

Expected: exit 0.

- [ ] **Step 2: Run the sole merged coverage enforcement command.**

Run: `./scripts/test-all.sh`

Expected: `Merged coverage: line=100% branch=100% method=100%`.

- [ ] **Step 3: Commit the verified slice.**

Run: `git add src/DataPitcher.Providers.PostgreSql tests/DataPitcher.Providers.PostgreSql.IntegrationTests && git commit -m "test: cover postgres transfer execution"`

## Self-Review

- [ ] Covered: COPY abort, atomic checkpoint/fence, commit-gap recovery, keyset resume, `RETURNING` policies, committed StrictExact keys, side-effect blocking, protected columns, both sequence directions, and merged 100 percent coverage.
- [ ] Deferred: SQL Server, workers/pipeline, source materialization, control persistence, checksums, foreign-key verification, cycle execution, and unrelated-writer claims.
- [ ] Checked cross-task type and method names: `PostgreSqlWriteTable`, `PostgreSqlTransferBatch`, `PostgreSqlExecutionContext`, checkpoint `InitializeAsync`/`AdvanceAsync`, `StageAsync`, `ApplyAsync`, `ExecuteAsync`/`RecoverAsync`, `PostgreSqlKeysetSeek.Build`, `PostgreSqlStrictExact`, and `PostgreSqlSequenceRealigner.RealignAsync`.
