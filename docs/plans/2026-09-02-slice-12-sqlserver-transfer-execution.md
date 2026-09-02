# DataPitcher Slice 12: SQL Server Transfer Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute fenced SQL Server transfer batches atomically, recover from the target checkpoint, and prove committed direct writes equal the sealed manifest in StrictExact mode.

**Architecture:** The SQL Server provider bulk-copies each materialized batch into a DataPitcher-owned typed staging table under one caller-owned `SqlTransaction`, then applies it with separate set-based UPDATE and INSERT statements. The business writes, `OUTPUT INTO` capture, and authoritative target checkpoint update commit together; the control-database mirror is post-commit and derived only. Recovery reads that target checkpoint and resumes the source after its encoded stable key using a keyset predicate.

**Tech Stack:** .NET 10; C#; Microsoft.Data.SqlClient **7.0.2**; SQL Server 2022; Testcontainers.MsSql **4.14.0**; xUnit **2.9.3**; coverlet/reportgenerator.

---

## File Structure

- `src/DataPitcher.Providers.SqlServer/SqlServerTransferModels.cs` — immutable write metadata, batches, checkpoint contracts, exceptions, and stable-key binary codec.
- `src/DataPitcher.Providers.SqlServer/SqlServerTransferSchemaReader.cs` — catalog-derived SQL types, identity/computed/rowversion flags, and binary text collation metadata for writable target columns.
- `src/DataPitcher.Providers.SqlServer/SqlServerTargetCheckpointStore.cs` — target-owned checkpoint DDL, initialization, reading, and fenced conditional advance.
- `src/DataPitcher.Providers.SqlServer/SqlServerBatchStageWriter.cs` — direct `SqlBulkCopy` staging with explicit mappings and an external transaction.
- `src/DataPitcher.Providers.SqlServer/SqlServerBatchApplier.cs` — typed staging apply, `OUTPUT INTO` capture, and durable affected-key recording.
- `src/DataPitcher.Providers.SqlServer/SqlServerTransferExecutor.cs`, `SqlServerKeysetSeek.cs` — atomic batch orchestration, target-only recovery, and source seek SQL.
- `src/DataPitcher.Providers.SqlServer/SqlServerStrictExact.cs` — side-effect preflight, exact set comparison, and conditional identity reseeding.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferTestData.cs` — shared transfer test builders.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferModelsTests.cs`, `SqlServerTargetCheckpointStoreTests.cs`, `SqlServerBatchExecutionTests.cs`, `SqlServerTransferRecoveryTests.cs`, `SqlServerStrictExactTests.cs` — provider-only integration coverage.

## Scope and Deferrals

This SQL Server mirror consumes sealed addresses/keys/batches/policies/context. Materialization, sizing, leasing, control persistence, checksums, integrity scans, cycles, and orchestration remain deferred.

`Microsoft.Data.SqlClient` **7.0.2** and Testcontainers.MsSql **4.14.0** are pinned; do not add LINQ to DB bulk copy. ADR 0005 requires direct `SqlBulkCopy` with a non-null external transaction, or earlier batches can persist after failure. Set `BatchSize = 0`, timeout 30, and streaming true. Default options leave identity, constraints, triggers, table lock, nulls, and internal transactions off.

The existing parallel-disabled `SqlServer closure` fixture shares separate source/target containers and fresh databases; do not add fixture churn. SQL Server runs under arm64 translation here, is ready in about 8 seconds, uses 1.08 GiB, and this lane takes about 4:40.

Test every new public member here: this assembly needs 100% line/branch/method coverage. Warnings and xUnit analyzers fail builds. xUnit 2.9.3 `Assert.NotNull` returns void; then use `!`. Prefer predicate `Assert.Single`/`DoesNotContain`; avoid keyword patterns and target-typed `new()` in `params` calls.

Use only `OUTPUT INTO`, into an unconstrained temporary destination: no triggers, FK participation, CHECKs, or rules. Confirmed failures are bare-output/trigger 334, destination trigger 331, FK 332, and CHECK/rule 333. Output precedes triggers and is trusted only after commit. Use UPDATE then INSERT-WHERE-NOT-EXISTS with `UPDLOCK,HOLDLOCK`, never `MERGE`.

## Tasks

### Task 1: Define SQL Server transfer contracts and target write metadata

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerTransferModels.cs`
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerTransferSchemaReader.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferTestData.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferModelsTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferModelsTests.cs`

- [ ] **Step 1: Write the failing transfer-contract and catalog tests.**

```csharp
using System.Data; using DataPitcher.Core.Identity; using DataPitcher.Core.Plans; using DataPitcher.Providers.SqlServer; using Microsoft.Data.SqlClient; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerTransferModelsTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task ReadAsync_MapsWritableColumnsAndProtectsTransferColumns()
    { await using var s = await fixture.CreateScopeAsync(); await s.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id bigint IDENTITY PRIMARY KEY, code nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL, stamp rowversion, computed AS LEN(code));"); var t = await new SqlServerTransferSchemaReader(s.TargetConnectionString).ReadAsync("dbo", "transfer_rows", ["id"], CancellationToken.None); Assert.Equal(SqlDbType.BigInt, t.Column("id").ProviderType); Assert.True(t.Column("id").IsIdentity); Assert.True(t.Column("stamp").IsRowVersion); Assert.True(t.Column("computed").IsComputed); Assert.Single(t.UpdateColumns, c => c.Name == "code"); }
    [Fact] public void StableKeys_RoundTripAndRequireAKey()
    { var t = new SqlServerWriteTable(new TableAddress("dbo", "rows"), [new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null)]); var k = new StableKey([new KeyComponent("id", 7)]); Assert.Equal(k, SqlServerStableKeyCodec.Decode(SqlServerStableKeyCodec.Encode(k, t), t)); Assert.Throws<ArgumentException>(() => new SqlServerWriteTable(new TableAddress("dbo", "no_key"), [new("code", "nvarchar(64)", typeof(string), SqlDbType.NVarChar, false, false, false, false, false, null)])); }
}
```

- [ ] **Step 2: Run the focused tests and confirm the transfer contracts are absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTransferModelsTests"`

Expected: compilation fails with CS0246 stating that `SqlServerTransferSchemaReader`, `SqlServerWriteTable`, and `SqlServerStableKeyCodec` could not be found.

- [ ] **Step 3: Add the contracts, bounded native codec, catalog mapping, and test builders.**

```csharp
// SqlServerTransferModels.cs
using System.Buffers; using System.Buffers.Binary; using System.Data; using System.Text;
using DataPitcher.Core.Identity; using DataPitcher.Core.Plans; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public enum SqlServerConflictPolicy { InsertOnly, SkipExisting, Upsert }
public sealed record SqlServerWriteColumn(string Name, string StoreType, Type ClrType, SqlDbType ProviderType, bool IsStableKey, bool IsIdentity, bool IsComputed, bool IsRowVersion, bool IsNullable, string? Collation);
public sealed class SqlServerWriteTable
{
    public SqlServerWriteTable(TableAddress target, IEnumerable<SqlServerWriteColumn> columns) { Target = target; Columns = Array.AsReadOnly(columns.ToArray()); StableKeyColumns = Array.AsReadOnly(Columns.Where(column => column.IsStableKey).ToArray()); InsertColumns = Array.AsReadOnly(Columns.Where(column => !column.IsComputed && !column.IsRowVersion).ToArray()); UpdateColumns = Array.AsReadOnly(InsertColumns.Where(column => !column.IsStableKey && !column.IsIdentity).ToArray()); if (StableKeyColumns.Count == 0) throw new ArgumentException("A write table requires a stable key."); }
    public TableAddress Target { get; } public IReadOnlyList<SqlServerWriteColumn> Columns { get; } public IReadOnlyList<SqlServerWriteColumn> StableKeyColumns { get; } public IReadOnlyList<SqlServerWriteColumn> InsertColumns { get; } public IReadOnlyList<SqlServerWriteColumn> UpdateColumns { get; }
    public SqlServerWriteColumn Column(string name) => Columns.Single(column => StringComparer.Ordinal.Equals(column.Name, name));
}
public sealed class SqlServerTransferRow { public SqlServerTransferRow(StableKey stableKey, IReadOnlyDictionary<string, object?> values) { StableKey = stableKey; Values = new Dictionary<string, object?>(values, StringComparer.Ordinal); } public StableKey StableKey { get; } public IReadOnlyDictionary<string, object?> Values { get; } }
public sealed class SqlServerTransferBatch { public SqlServerTransferBatch(long sequence, IEnumerable<SqlServerTransferRow> rows, StableKey lastStableKey, SqlServerConflictPolicy policy) { Sequence = sequence; Rows = Array.AsReadOnly(rows.ToArray()); LastStableKey = lastStableKey; Policy = policy; } public long Sequence { get; } public IReadOnlyList<SqlServerTransferRow> Rows { get; } public StableKey LastStableKey { get; } public SqlServerConflictPolicy Policy { get; } }
public sealed record SqlServerExecutionContext(Guid JobId, Guid RunId, long FenceToken, string ManifestHash);
public sealed record SqlServerTargetCheckpoint(Guid JobId, Guid RunId, long LastBatchSequence, byte[] LastStableKey, long CumulativeAffected, long CumulativeInserts, long CumulativeUpdates, string ManifestHash, long FenceToken);
public sealed record SqlServerResumePoint(long NextBatchSequence, StableKey? AfterStableKey); public sealed record SqlServerBatchCommit(long Sequence, long Affected, long Inserts, long Updates);
public interface ISqlServerDerivedCheckpointMirror { Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken); }
public interface ISqlServerAfterTargetCommitBarrier { Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken); }
public sealed class SqlServerFenceLostException() : InvalidOperationException("The target checkpoint fence token no longer belongs to this worker.");
public sealed class SqlServerManifestMismatchException() : InvalidOperationException("The target checkpoint manifest hash differs from the sealed manifest.");
public sealed class SqlServerStrictExactBlockedException(string reason) : InvalidOperationException(reason);
public static class SqlServerStableKeyCodec
{
    public static byte[] Encode(StableKey key, SqlServerWriteTable table) { var buffer = new ArrayBufferWriter<byte>(); foreach (var column in table.StableKeyColumns) { var value = key.Components.Single(component => component.Column == column.Name).Value ?? throw new ArgumentException("Stable-key values cannot be null."); Write(buffer, value, column.ProviderType); } return buffer.WrittenSpan.ToArray(); }
    public static StableKey Decode(byte[] bytes, SqlServerWriteTable table) { var offset = 0; var components = new List<KeyComponent>(); foreach (var column in table.StableKeyColumns) components.Add(new KeyComponent(column.Name, Read(bytes, ref offset, column.ProviderType))); if (offset != bytes.Length) throw new ArgumentException("Stable-key encoding has trailing bytes."); return new StableKey(components); }
    private static void Write(ArrayBufferWriter<byte> buffer, object value, SqlDbType type) { if (type == SqlDbType.Int && value is int integer) { var span = buffer.GetSpan(4); BinaryPrimitives.WriteInt32BigEndian(span, integer); buffer.Advance(4); return; } if (type == SqlDbType.BigInt && value is long bigint) { var span = buffer.GetSpan(8); BinaryPrimitives.WriteInt64BigEndian(span, bigint); buffer.Advance(8); return; } var text = type == SqlDbType.NVarChar && value is string stringValue ? Encoding.UTF8.GetBytes(stringValue) : throw new NotSupportedException($"Stable-key type {type} is not supported."); var length = buffer.GetSpan(4); BinaryPrimitives.WriteInt32BigEndian(length, text.Length); buffer.Advance(4); buffer.Write(text); }
    private static object Read(byte[] bytes, ref int offset, SqlDbType type) { if (type == SqlDbType.Int) { var result = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4; return result; } if (type == SqlDbType.BigInt) { var result = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, 8)); offset += 8; return result; } if (type == SqlDbType.NVarChar) { var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4; var result = Encoding.UTF8.GetString(bytes, offset, length); offset += length; return result; } throw new NotSupportedException($"Stable-key type {type} is not supported."); }
}

// SqlServerTransferSchemaReader.cs
using System.Data; using DataPitcher.Core.Plans; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerTransferSchemaReader(string connectionString)
{
    public async Task<SqlServerWriteTable> ReadAsync(string schema, string table, IReadOnlyCollection<string> stableKeys, CancellationToken cancellationToken)
    {
        const string sql = "SELECT c.name,ty.name,c.max_length,c.is_nullable,c.is_identity,c.is_computed,CASE WHEN ty.name='timestamp' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,c.collation_name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id";
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(cancellationToken); await using var command = new SqlCommand(sql, connection); command.Parameters.AddWithValue("@schema", schema); command.Parameters.AddWithValue("@table", table); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var columns = new List<SqlServerWriteColumn>();
        while (await reader.ReadAsync(cancellationToken)) { var name = reader.GetString(0); var typeName = reader.GetString(1); var computed = reader.GetBoolean(5); var rowVersion = reader.GetBoolean(6); var mapped = computed || rowVersion ? (typeof(byte[]), SqlDbType.Variant) : Map(typeName); columns.Add(new SqlServerWriteColumn(name, StoreType(typeName, reader.GetInt16(2)), mapped.Item1, mapped.Item2, stableKeys.Contains(name, StringComparer.Ordinal), reader.GetBoolean(4), computed, rowVersion, reader.GetBoolean(3), reader.IsDBNull(7) ? null : reader.GetString(7))); }
        return new SqlServerWriteTable(new TableAddress(schema, table), columns);
    }
    private static (Type, SqlDbType) Map(string type) => type switch { "int" => (typeof(int), SqlDbType.Int), "bigint" => (typeof(long), SqlDbType.BigInt), "nvarchar" => (typeof(string), SqlDbType.NVarChar), _ => throw new NotSupportedException($"SQL Server transfer column type '{type}' is not supported.") };
    private static string StoreType(string type, short length) => type == "nvarchar" ? length == -1 ? "nvarchar(max)" : $"nvarchar({length / 2})" : type;
}

// SqlServerTransferTestData.cs
using System.Data; using DataPitcher.Core.Identity; using DataPitcher.Core.Plans; using DataPitcher.Providers.SqlServer; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
internal static class SqlServerTransferTestData
{
    public static SqlServerWriteTable Table() => new(new TableAddress("dbo", "transfer_rows"), [new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null), new("code", "nvarchar(64)", typeof(string), SqlDbType.NVarChar, false, false, false, false, false, "Latin1_General_100_BIN2")]);
    public static SqlServerWriteTable TextKeyTable() => new(new TableAddress("dbo", "transfer_rows"), [new("code", "nvarchar(64)", typeof(string), SqlDbType.NVarChar, true, false, false, false, false, "Latin1_General_100_BIN2")]);
    public static SqlServerTransferBatch Batch(long sequence, params (int Id, string Code)[] rows) => new(sequence, rows.Select(row => new SqlServerTransferRow(new StableKey([new KeyComponent("id", row.Id)]), new Dictionary<string, object?> { ["id"] = row.Id, ["code"] = row.Code })), new StableKey([new KeyComponent("id", rows.Last().Id)]), SqlServerConflictPolicy.InsertOnly);
    public static SqlServerExecutionContext Context(long fence = 1) => new(Guid.NewGuid(), Guid.NewGuid(), fence, "sealed-manifest-hash");
}
```

The codec supports only `int`, `bigint`, and `nvarchar`; unknown writable types fail before staging. Cover null/wrong/unsupported keys, trailing bytes, `nvarchar(max)`, and each mapping arm here.

- [ ] **Step 4: Run the focused tests and confirm every public contract passes.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTransferModelsTests"`

Expected: `Passed!  - Failed:     0` and no warning or analyzer diagnostic.

- [ ] **Step 5: Commit the contracts and transfer metadata reader.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerTransferModels.cs src/DataPitcher.Providers.SqlServer/SqlServerTransferSchemaReader.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferTestData.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferModelsTests.cs && git commit -m "feat: define sqlserver transfer contracts"`

### Task 2: Persist and fence the authoritative target checkpoint

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerTargetCheckpointStore.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTargetCheckpointStoreTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTargetCheckpointStoreTests.cs`

- [ ] **Step 1: Write the failing initialization, manifest, and fence tests.**

```csharp
using System.Data; using DataPitcher.Providers.SqlServer; using Microsoft.Data.SqlClient; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerTargetCheckpointStoreTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task AdvanceAsync_RecordsTheBatchAndRejectsASupersededFence()
    { await using var s = await fixture.CreateScopeAsync(); var old = SqlServerTransferTestData.Context(); var current = old with { FenceToken = 2 }; var store = new SqlServerTargetCheckpointStore(s.TargetConnectionString); await store.InitializeAsync(old, CancellationToken.None); await using var c = new SqlConnection(s.TargetConnectionString); await c.OpenAsync(); await using var tx = (SqlTransaction)await c.BeginTransactionAsync(CancellationToken.None); await store.AdvanceAsync(c, tx, old, SqlServerTransferTestData.Table(), SqlServerTransferTestData.Batch(0, (1, "one")), 1, 1, 0, CancellationToken.None); await tx.CommitAsync(CancellationToken.None); var checkpoint = await store.ReadAsync(old.JobId, old.RunId, CancellationToken.None); Assert.NotNull(checkpoint); Assert.Equal(0, checkpoint!.LastBatchSequence); await store.InitializeAsync(current, CancellationToken.None); await using var stale = (SqlTransaction)await c.BeginTransactionAsync(CancellationToken.None); await Assert.ThrowsAsync<SqlServerFenceLostException>(() => store.AdvanceAsync(c, stale, old, SqlServerTransferTestData.Table(), SqlServerTransferTestData.Batch(1, (2, "two")), 1, 1, 0, CancellationToken.None)); }
}
```

- [ ] **Step 2: Run the checkpoint tests and confirm the store is absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTargetCheckpointStoreTests"`

Expected: compilation fails with CS0246 that `SqlServerTargetCheckpointStore` could not be found.

- [ ] **Step 3: Implement target DDL, serializable initialization, target reads, and the zero-row fence assertion.**

```csharp
using System.Data; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerTargetCheckpointStore(string targetConnectionString)
{
    private const string Name = "[datapitcher].[transfer_checkpoints]";
    public async Task InitializeAsync(SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken); await EnsureAsync(connection, transaction, cancellationToken);
        var existing = await ReadAsync(connection, transaction, context.JobId, context.RunId, cancellationToken);
        if (existing is null) await ExecuteAsync(connection, transaction, "INSERT " + Name + " (job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token) VALUES (@job,@run,-1,0x,0,0,0,@hash,@fence)", context, cancellationToken);
        else if (!StringComparer.Ordinal.Equals(existing.ManifestHash, context.ManifestHash)) throw new SqlServerManifestMismatchException();
        else if (existing.FenceToken > context.FenceToken) throw new SqlServerFenceLostException();
        else if (existing.FenceToken < context.FenceToken && await ExecuteAsync(connection, transaction, "UPDATE " + Name + " SET fence_token=@fence WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token<@fence", context, cancellationToken) != 1) throw new SqlServerFenceLostException();
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<SqlServerTargetCheckpoint?> ReadAsync(Guid jobId, Guid runId, CancellationToken cancellationToken) { await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); return await ReadAsync(connection, null, jobId, runId, cancellationToken); }
    public async Task AdvanceAsync(SqlConnection connection, SqlTransaction transaction, SqlServerExecutionContext context, SqlServerWriteTable table, SqlServerTransferBatch batch, long affected, long inserts, long updates, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE [datapitcher].[transfer_checkpoints] SET last_batch_sequence=@sequence,last_stable_key=@key,cumulative_affected=cumulative_affected+@affected,cumulative_inserts=cumulative_inserts+@inserts,cumulative_updates=cumulative_updates+@updates WHERE job_id=@job AND run_id=@run AND manifest_hash=@hash AND fence_token=@fence AND last_batch_sequence=@previous";
        await using var command = new SqlCommand(sql, connection, transaction); AddContext(command, context); command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = batch.Sequence; command.Parameters.Add("@key", SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(batch.LastStableKey, table); command.Parameters.Add("@affected", SqlDbType.BigInt).Value = affected; command.Parameters.Add("@inserts", SqlDbType.BigInt).Value = inserts; command.Parameters.Add("@updates", SqlDbType.BigInt).Value = updates; command.Parameters.Add("@previous", SqlDbType.BigInt).Value = batch.Sequence - 1;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new SqlServerFenceLostException();
    }
    private static async Task EnsureAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) { await using var command = new SqlCommand("IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'[datapitcher].[transfer_checkpoints]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_checkpoints] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,last_batch_sequence bigint NOT NULL,last_stable_key varbinary(max) NOT NULL,cumulative_affected bigint NOT NULL,cumulative_inserts bigint NOT NULL,cumulative_updates bigint NOT NULL,manifest_hash nvarchar(128) NOT NULL,fence_token bigint NOT NULL,PRIMARY KEY(job_id,run_id));", connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task<SqlServerTargetCheckpoint?> ReadAsync(SqlConnection connection, SqlTransaction? transaction, Guid jobId, Guid runId, CancellationToken cancellationToken) { await using var command = new SqlCommand("SELECT job_id,run_id,last_batch_sequence,last_stable_key,cumulative_affected,cumulative_inserts,cumulative_updates,manifest_hash,fence_token FROM " + Name + " WHERE job_id=@job AND run_id=@run", connection, transaction); command.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = jobId; command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = runId; await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? new SqlServerTargetCheckpoint(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8)) : null; }
    private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, SqlServerExecutionContext context, CancellationToken cancellationToken) { await using var command = new SqlCommand(sql, connection, transaction); AddContext(command, context); return await command.ExecuteNonQueryAsync(cancellationToken); }
    private static void AddContext(SqlCommand command, SqlServerExecutionContext context) { command.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = context.JobId; command.Parameters.Add("@run", SqlDbType.UniqueIdentifier).Value = context.RunId; command.Parameters.Add("@hash", SqlDbType.NVarChar, 128).Value = context.ManifestHash; command.Parameters.Add("@fence", SqlDbType.BigInt).Value = context.FenceToken; }
}
```

The target row holds job/run, sequence, key, counters, seal, and fence. Initialization is serializable; advance conditionally matches seal, fence, and prior sequence, so zero rows aborts the caller transaction.

- [ ] **Step 4: Run the checkpoint tests and confirm checkpoint persistence and fencing pass.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTargetCheckpointStoreTests"`

Expected: `Passed!  - Failed:     0`; the stale conditional update affects zero rows and throws `SqlServerFenceLostException`.

- [ ] **Step 5: Commit the authoritative checkpoint store.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerTargetCheckpointStore.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTargetCheckpointStoreTests.cs && git commit -m "feat: fence sqlserver target checkpoints"`

### Task 3: Stage with direct SqlBulkCopy and apply with OUTPUT INTO

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerBatchStageWriter.cs`
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerBatchApplier.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerBatchExecutionTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerBatchExecutionTests.cs`

- [ ] **Step 1: Write the failing native writer, policy, and capture-boundary tests.**

```csharp
using System.Data; using DataPitcher.Providers.SqlServer; using Microsoft.Data.SqlClient; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerBatchExecutionTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task StageAsync_WhenTheSecondNativeCopyFails_RollbackLeavesNoBusinessRowOrCheckpointAdvance()
    {
        await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(2) NOT NULL);"); var context = SqlServerTransferTestData.Context(); var table = new SqlServerWriteTable(SqlServerTransferTestData.Table().Target, [new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null), new("code", "nvarchar(2)", typeof(string), SqlDbType.NVarChar, false, false, false, false, false, "Latin1_General_100_BIN2")]); var store = new SqlServerTargetCheckpointStore(scope.TargetConnectionString); await store.InitializeAsync(context, CancellationToken.None);
        await using var connection = new SqlConnection(scope.TargetConnectionString); await connection.OpenAsync(); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None); var writer = new SqlServerBatchStageWriter(); await writer.StageAsync(connection, transaction, context, table, SqlServerTransferTestData.Batch(0, (1, "ok")), CancellationToken.None); await Assert.ThrowsAsync<SqlException>(() => writer.StageAsync(connection, transaction, context, table, SqlServerTransferTestData.Batch(1, (2, "too-long")), CancellationToken.None)); await transaction.RollbackAsync(CancellationToken.None);
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.transfer_rows")); var checkpoint = await store.ReadAsync(context.JobId, context.RunId, CancellationToken.None); Assert.NotNull(checkpoint); Assert.Equal(-1, checkpoint!.LastBatchSequence);
    }
    [Theory] [InlineData(SqlServerConflictPolicy.InsertOnly, 2, 0, 2)] [InlineData(SqlServerConflictPolicy.SkipExisting, 1, 0, 1)] [InlineData(SqlServerConflictPolicy.Upsert, 2, 1, 1)]
    public async Task ApplyAsync_UsesSeparateStatementsAndRecordsOnlyCommittedAffectedKeys(SqlServerConflictPolicy policy, int affected, int updates, int inserts)
    {
        await using var scope = await fixture.CreateScopeAsync(); await scope.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL);"); if (policy != SqlServerConflictPolicy.InsertOnly) await scope.ExecuteTargetAsync("INSERT dbo.transfer_rows VALUES (1,N'old');"); var context = SqlServerTransferTestData.Context(); var batch = SqlServerTransferTestData.Batch(0, (1, "new"), (2, "two")); batch = new SqlServerTransferBatch(batch.Sequence, batch.Rows, batch.LastStableKey, policy);
        await using var connection = new SqlConnection(scope.TargetConnectionString); await connection.OpenAsync(); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None); await new SqlServerBatchStageWriter().StageAsync(connection, transaction, context, SqlServerTransferTestData.Table(), batch, CancellationToken.None); var result = await new SqlServerBatchApplier().ApplyAsync(connection, transaction, context, SqlServerTransferTestData.Table(), batch, CancellationToken.None); Assert.Equal(affected, result.Affected); Assert.Equal(inserts, result.Inserts); Assert.Equal(updates, result.Updates); await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal(affected, await scope.ScalarTargetAsync<int>($"SELECT COUNT(*) FROM [datapitcher].[transfer_affected_keys] WHERE job_id='{context.JobId}'"));
    }
}
```

- [ ] **Step 2: Run the batch tests and confirm the writer and applier are absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerBatchExecutionTests"`

Expected: compilation fails with CS0246 that `SqlServerBatchStageWriter` and `SqlServerBatchApplier` could not be found.

- [ ] **Step 3: Implement the native staged writer, temporary OUTPUT INTO destinations, and separate DML.**

```csharp
// SqlServerBatchStageWriter.cs
using System.Data; using System.Security.Cryptography; using System.Text; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerBatchStageWriter
{
    public async Task StageAsync(SqlConnection connection, SqlTransaction transaction, SqlServerExecutionContext context, SqlServerWriteTable table, SqlServerTransferBatch batch, CancellationToken cancellationToken)
    {
        var stage = StageName(table); var columns = table.InsertColumns; var declarations = string.Join(",", new[] { "[job_id] uniqueidentifier NOT NULL", "[run_id] uniqueidentifier NOT NULL", "[fence_token] bigint NOT NULL", "[batch_sequence] bigint NOT NULL" }.Concat(columns.Select(column => SqlServerIdentifier.Quote(column.Name) + " " + column.StoreType + (column.IsNullable ? " NULL" : " NOT NULL"))));
        await using (var create = new SqlCommand("IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'" + stage.Replace("'", "''", StringComparison.Ordinal) + "',N'U') IS NULL CREATE TABLE " + stage + " (" + declarations + ");", connection, transaction)) await create.ExecuteNonQueryAsync(cancellationToken);
        var data = new DataTable(); data.Columns.Add("job_id", typeof(Guid)); data.Columns.Add("run_id", typeof(Guid)); data.Columns.Add("fence_token", typeof(long)); data.Columns.Add("batch_sequence", typeof(long)); foreach (var column in columns) data.Columns.Add(column.Name, column.ClrType);
        foreach (var row in batch.Rows) { var values = data.NewRow(); values["job_id"] = context.JobId; values["run_id"] = context.RunId; values["fence_token"] = context.FenceToken; values["batch_sequence"] = batch.Sequence; foreach (var column in columns) values[column.Name] = row.Values[column.Name] ?? DBNull.Value; data.Rows.Add(values); }
        using var reader = data.CreateDataReader(); using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = stage, BatchSize = 0, BulkCopyTimeout = 30, EnableStreaming = true }; for (var ordinal = 0; ordinal < data.Columns.Count; ordinal++) bulk.ColumnMappings.Add(ordinal, data.Columns[ordinal].ColumnName); await bulk.WriteToServerAsync(reader, cancellationToken);
    }
    public static string StageName(SqlServerWriteTable table) => SqlServerIdentifier.Qualified("datapitcher", "stage_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table.Target.Schema + "\u001f" + table.Target.Name))).ToLowerInvariant());
}

// SqlServerBatchApplier.cs
using DataPitcher.Core.Identity; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed record SqlServerApplyResult(long Affected, long Inserts, long Updates);
public sealed class SqlServerBatchApplier
{
    public async Task<SqlServerApplyResult> ApplyAsync(SqlConnection connection, SqlTransaction transaction, SqlServerExecutionContext context, SqlServerWriteTable table, SqlServerTransferBatch batch, CancellationToken cancellationToken)
    {
        await EnsureLedgerAsync(connection, transaction, cancellationToken); var updates = batch.Policy == SqlServerConflictPolicy.Upsert && table.UpdateColumns.Count != 0 ? await ApplyAndRecordAsync(connection, transaction, UpdateSql(table), "#updated", "UPDATE", context, table, batch.Sequence, cancellationToken) : 0; if (table.InsertColumns.Any(column => column.IsIdentity)) await SetIdentityInsertAsync(connection, transaction, table, true, cancellationToken);
        try { var inserts = await ApplyAndRecordAsync(connection, transaction, InsertSql(table, batch.Policy), "#inserted", "INSERT", context, table, batch.Sequence, cancellationToken); return new SqlServerApplyResult(inserts + updates, inserts, updates); }
        finally { if (table.InsertColumns.Any(column => column.IsIdentity)) await SetIdentityInsertAsync(connection, transaction, table, false, cancellationToken); }
    }
    private static async Task<long> ApplyAndRecordAsync(SqlConnection connection, SqlTransaction transaction, string sql, string capture, string action, SqlServerExecutionContext context, SqlServerWriteTable table, long sequence, CancellationToken cancellationToken)
    {
        var declarations = string.Join(",", table.StableKeyColumns.Select((column, index) => "[k" + index + "] " + column.StoreType + " NOT NULL")); await using (var create = new SqlCommand("CREATE TABLE " + capture + " (" + declarations + ");", connection, transaction)) await create.ExecuteNonQueryAsync(cancellationToken); await using (var apply = new SqlCommand(sql.Replace("{capture}", capture, StringComparison.Ordinal), connection, transaction)) { AddBatch(apply, context, sequence); await apply.ExecuteNonQueryAsync(cancellationToken); }
        await using var read = new SqlCommand("SELECT " + string.Join(",", table.StableKeyColumns.Select((_, index) => "[k" + index + "]")) + " FROM " + capture, connection, transaction); await using var rows = await read.ExecuteReaderAsync(cancellationToken); var keys = new List<StableKey>(); while (await rows.ReadAsync(cancellationToken)) keys.Add(new StableKey(table.StableKeyColumns.Select((column, index) => new KeyComponent(column.Name, rows.GetValue(index))))); await rows.CloseAsync(); foreach (var key in keys) await RecordAsync(connection, transaction, context, table, key, action, cancellationToken); return keys.Count;
    }
    private static string UpdateSql(SqlServerWriteTable table) { var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name); var stage = SqlServerBatchStageWriter.StageName(table); var set = string.Join(",", table.UpdateColumns.Select(column => "t." + SqlServerIdentifier.Quote(column.Name) + "=s." + SqlServerIdentifier.Quote(column.Name))); var output = string.Join(",", table.StableKeyColumns.Select(column => "INSERTED." + SqlServerIdentifier.Quote(column.Name))); var join = Join(table.StableKeyColumns, "s", "t"); return "UPDATE t SET " + set + " OUTPUT " + output + " INTO {capture} FROM " + target + " t JOIN " + stage + " s ON " + join + " WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence"; }
    private static string InsertSql(SqlServerWriteTable table, SqlServerConflictPolicy policy) { var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name); var stage = SqlServerBatchStageWriter.StageName(table); var columns = string.Join(",", table.InsertColumns.Select(column => SqlServerIdentifier.Quote(column.Name))); var output = string.Join(",", table.StableKeyColumns.Select(column => "INSERTED." + SqlServerIdentifier.Quote(column.Name))); var predicate = policy == SqlServerConflictPolicy.InsertOnly ? "" : " AND NOT EXISTS (SELECT 1 FROM " + target + " t WITH (UPDLOCK,HOLDLOCK) WHERE " + Join(table.StableKeyColumns, "s", "t") + ")"; return "INSERT " + target + " (" + columns + ") OUTPUT " + output + " INTO {capture} SELECT " + columns + " FROM " + stage + " s WHERE s.job_id=@job AND s.run_id=@run AND s.fence_token=@fence AND s.batch_sequence=@sequence" + predicate; }
    private static string Join(IEnumerable<SqlServerWriteColumn> columns, string left, string right) => string.Join(" AND ", columns.Select(column => left + "." + SqlServerIdentifier.Quote(column.Name) + "=" + right + "." + SqlServerIdentifier.Quote(column.Name)));
    private static async Task EnsureLedgerAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) { await using var command = new SqlCommand("IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'[datapitcher].[transfer_affected_keys]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_affected_keys] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL,action_name nvarchar(6) NOT NULL); IF OBJECT_ID(N'[datapitcher].[transfer_write_manifest]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_write_manifest] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL);", connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task RecordAsync(SqlConnection connection, SqlTransaction transaction, SqlServerExecutionContext context, SqlServerWriteTable table, StableKey key, string action, CancellationToken cancellationToken) { await using var command = new SqlCommand("INSERT [datapitcher].[transfer_affected_keys] VALUES (@job,@run,@schema,@table,@key,@action)", connection, transaction); command.Parameters.AddWithValue("@job", context.JobId); command.Parameters.AddWithValue("@run", context.RunId); command.Parameters.AddWithValue("@schema", table.Target.Schema); command.Parameters.AddWithValue("@table", table.Target.Name); command.Parameters.Add("@key", System.Data.SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(key, table); command.Parameters.AddWithValue("@action", action); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task SetIdentityInsertAsync(SqlConnection connection, SqlTransaction transaction, SqlServerWriteTable table, bool enabled, CancellationToken cancellationToken) { await using var command = new SqlCommand("SET IDENTITY_INSERT " + SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name) + (enabled ? " ON" : " OFF"), connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static void AddBatch(SqlCommand command, SqlServerExecutionContext context, long sequence) { command.Parameters.AddWithValue("@job", context.JobId); command.Parameters.AddWithValue("@run", context.RunId); command.Parameters.AddWithValue("@fence", context.FenceToken); command.Parameters.AddWithValue("@sequence", sequence); }
}
```

Use the concrete connection, transaction, `DataTableReader`, and ordinal mappings. Test nulls, `StageName`, identity insertion, InsertOnly conflict, and zero mutable Upsert columns.

- [ ] **Step 4: Run the native writer and capture tests and confirm they pass.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerBatchExecutionTests"`

Expected: `Passed!  - Failed:     0`; captured keys are inspected only after commit.

- [ ] **Step 5: Commit the direct SqlBulkCopy writer and OUTPUT INTO applier.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerBatchStageWriter.cs src/DataPitcher.Providers.SqlServer/SqlServerBatchApplier.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerBatchExecutionTests.cs && git commit -m "feat: apply sqlserver transfer batches"`

### Task 4: Recover from target state and seek the next source batch

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerTransferExecutor.cs`
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerKeysetSeek.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferRecoveryTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferRecoveryTests.cs`

- [ ] **Step 1: Write the failing deterministic crash, stale-worker, and keyset tests.**

```csharp
using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerTransferRecoveryTests(SqlServerClosureFixture fixture)
{
    [Fact] public void Build_UsesLexicographicBinaryCollationKeysetSeekingWithoutOffset()
    { var q = SqlServerKeysetSeek.Build(SqlServerTransferTestData.TextKeyTable(), new DataPitcher.Core.Identity.StableKey([new("code", "B")]), 100); Assert.Contains("TOP (@limit)", q.Sql, StringComparison.Ordinal); Assert.DoesNotContain("OFFSET", q.Sql, StringComparison.OrdinalIgnoreCase); }
}
```

- [ ] **Step 2: Run the recovery tests and confirm execution and seeking are absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTransferRecoveryTests"`

Expected: compilation fails with CS0246 that `SqlServerTransferExecutor` and `SqlServerKeysetSeek` could not be found.

- [ ] **Step 3: Implement the atomic executor, target-only recovery, and source keyset SQL.**

```csharp
// SqlServerTransferExecutor.cs
using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerTransferExecutor
{
    private readonly string _targetConnectionString; private readonly ISqlServerDerivedCheckpointMirror _mirror; private readonly ISqlServerAfterTargetCommitBarrier _barrier; private readonly SqlServerTargetCheckpointStore _checkpoints;
    public SqlServerTransferExecutor(string targetConnectionString, ISqlServerDerivedCheckpointMirror mirror, ISqlServerAfterTargetCommitBarrier barrier) { _targetConnectionString = targetConnectionString; _mirror = mirror; _barrier = barrier; _checkpoints = new SqlServerTargetCheckpointStore(targetConnectionString); }
    public Task InitializeAsync(SqlServerExecutionContext context, CancellationToken cancellationToken) => _checkpoints.InitializeAsync(context, cancellationToken);
    public async Task<SqlServerBatchCommit> ExecuteAsync(SqlServerExecutionContext context, SqlServerWriteTable table, SqlServerTransferBatch batch, CancellationToken cancellationToken)
    {
        await InitializeAsync(context, cancellationToken); await using var connection = new SqlConnection(_targetConnectionString); await connection.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken); await new SqlServerBatchStageWriter().StageAsync(connection, transaction, context, table, batch, cancellationToken); var result = await new SqlServerBatchApplier().ApplyAsync(connection, transaction, context, table, batch, cancellationToken); await _checkpoints.AdvanceAsync(connection, transaction, context, table, batch, result.Affected, result.Inserts, result.Updates, cancellationToken); await transaction.CommitAsync(cancellationToken);
        var checkpoint = await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken); if (checkpoint is null) throw new InvalidOperationException("Committed target checkpoint was not found."); await _barrier.WaitAsync(checkpoint, cancellationToken); await _mirror.WriteAsync(checkpoint, cancellationToken); return new SqlServerBatchCommit(batch.Sequence, result.Affected, result.Inserts, result.Updates);
    }
    public async Task<SqlServerResumePoint> RecoverAsync(SqlServerExecutionContext context, SqlServerWriteTable table, CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.ReadAsync(context.JobId, context.RunId, cancellationToken) ?? throw new InvalidOperationException("Target checkpoint was not initialized."); if (!StringComparer.Ordinal.Equals(checkpoint.ManifestHash, context.ManifestHash)) throw new SqlServerManifestMismatchException(); await _mirror.WriteAsync(checkpoint, cancellationToken); return new SqlServerResumePoint(checkpoint.LastBatchSequence + 1, checkpoint.LastBatchSequence < 0 ? null : SqlServerStableKeyCodec.Decode(checkpoint.LastStableKey, table));
    }
}

// SqlServerKeysetSeek.cs
using DataPitcher.Core.Identity; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed record SqlServerSeekQuery(string Sql, IReadOnlyList<SqlParameter> Parameters);
public static class SqlServerKeysetSeek
{
    public static SqlServerSeekQuery Build(SqlServerWriteTable table, StableKey after, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit)); var columns = table.StableKeyColumns; var predicates = new List<string>(); var parameters = new List<SqlParameter>(); for (var index = 0; index < columns.Count; index++) { var equal = string.Join(" AND ", Enumerable.Range(0, index).Select(prior => Expression(columns[prior]) + "=@k" + prior)); predicates.Add((equal.Length == 0 ? "" : equal + " AND ") + Expression(columns[index]) + ">@k" + index); }
        for (var index = 0; index < columns.Count; index++) parameters.Add(new SqlParameter("@k" + index, columns[index].ProviderType) { Value = after.Components.Single(component => component.Column == columns[index].Name).Value! }); parameters.Add(new SqlParameter("@limit", System.Data.SqlDbType.Int) { Value = limit }); var select = string.Join(",", table.InsertColumns.Select(column => "s." + SqlServerIdentifier.Quote(column.Name))); return new SqlServerSeekQuery("SELECT TOP (@limit) " + select + " FROM " + SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name) + " s WHERE (" + string.Join(" OR ", predicates) + ") ORDER BY " + string.Join(",", columns.Select(Expression)), Array.AsReadOnly(parameters.ToArray()));
    }
    private static string Expression(SqlServerWriteColumn column) => "s." + SqlServerIdentifier.Quote(column.Name) + (column.ProviderType == System.Data.SqlDbType.NVarChar ? " COLLATE " + (column.Collation ?? throw new InvalidOperationException("Text stable keys require a catalog collation.")) : "");
}
```

The barrier signals only after commit; the crash test observes target data/checkpoint with zero mirror writes, releases a fault, and recovers from the target. Stage before superseding the fence, then prove zero-row advance rolls back the business write. Cover success, empty/mismatched recovery, composite/non-positive/missing-collation seeks, and no OFFSET.

- [ ] **Step 4: Run the recovery tests and confirm commit-gap recovery, fencing, and seeking pass.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerTransferRecoveryTests"`

Expected: `Passed!  - Failed:     0`; the stale transaction leaves zero business rows and the crash test resumes at sequence 1 after stable key 1.

- [ ] **Step 5: Commit target-based recovery and SQL Server keyset seeking.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerTransferExecutor.cs src/DataPitcher.Providers.SqlServer/SqlServerKeysetSeek.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerTransferRecoveryTests.cs && git commit -m "feat: recover sqlserver transfer batches"`

### Task 5: Block unsafe StrictExact plans, verify committed keys, and conditionally realign identities

**Files:**
- Create: `src/DataPitcher.Providers.SqlServer/SqlServerStrictExact.cs`
- Create: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStrictExactTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStrictExactTests.cs`

- [ ] **Step 1: Write failing side-effect, committed-key, and identity-direction tests.**

```csharp
using System.Data; using DataPitcher.Core.Identity; using DataPitcher.Providers.SqlServer; using Xunit;
namespace DataPitcher.Providers.SqlServer.IntegrationTests;
[Collection("SqlServer closure")]
public sealed class SqlServerStrictExactTests(SqlServerClosureFixture fixture)
{
    [Fact] public async Task EnsureAvailableAsync_WhenThePlannedTargetHasAnEnabledTrigger_RefusesStrictExact()
    { await using var s = await fixture.CreateScopeAsync(); await s.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64)); CREATE TRIGGER dbo.transfer_trigger ON dbo.transfer_rows AFTER INSERT AS SELECT 1;"); await Assert.ThrowsAsync<SqlServerStrictExactBlockedException>(() => new SqlServerStrictExact(s.TargetConnectionString).EnsureAvailableAsync(SqlServerTransferTestData.Table(), CancellationToken.None)); }
    [Fact] public async Task EnsureAvailableAsync_WhenThePlannedTargetHasAnInboundCascade_RefusesStrictExact()
    { await using var s = await fixture.CreateScopeAsync(); await s.ExecuteTargetAsync("CREATE TABLE dbo.transfer_rows (id int PRIMARY KEY,code nvarchar(64)); CREATE TABLE dbo.transfer_children (id int PRIMARY KEY,parent_id int REFERENCES dbo.transfer_rows(id) ON UPDATE CASCADE);"); await Assert.ThrowsAsync<SqlServerStrictExactBlockedException>(() => new SqlServerStrictExact(s.TargetConnectionString).EnsureAvailableAsync(SqlServerTransferTestData.Table(), CancellationToken.None)); }
}
```

- [ ] **Step 2: Run the StrictExact and identity tests and confirm the verifier is absent.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerStrictExactTests"`

Expected: compilation fails with CS0246 that `SqlServerStrictExact` and `SqlServerIdentityRealigner` could not be found.

- [ ] **Step 3: Implement side-effect preflight, post-commit set equality, and direction-aware conditional reseeding.**

```csharp
using System.Data; using System.Globalization; using DataPitcher.Core.Identity; using Microsoft.Data.SqlClient;
namespace DataPitcher.Providers.SqlServer;
public sealed class SqlServerStrictExact(string targetConnectionString)
{
    public async Task EnsureAvailableAsync(SqlServerWriteTable table, CancellationToken cancellationToken)
    {
        var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name); if (await ExistsAsync("SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(@target) AND is_disabled=0 AND is_ms_shipped=0", target, cancellationToken)) throw new SqlServerStrictExactBlockedException("StrictExact is blocked by a target trigger."); if (await ExistsAsync("SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID(@target) AND is_disabled=0 AND (delete_referential_action<>0 OR update_referential_action<>0)", target, cancellationToken)) throw new SqlServerStrictExactBlockedException("StrictExact is blocked by a target cascading write path.");
    }
    public async Task RecordPlannedAsync(SqlServerExecutionContext context, SqlServerWriteTable table, IReadOnlyCollection<StableKey> keys, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken); await EnsureLedgerAsync(connection, transaction, cancellationToken); foreach (var key in keys) { await using var command = new SqlCommand("INSERT [datapitcher].[transfer_write_manifest] VALUES (@job,@run,@schema,@table,@key)", connection, transaction); command.Parameters.AddWithValue("@job", context.JobId); command.Parameters.AddWithValue("@run", context.RunId); command.Parameters.AddWithValue("@schema", table.Target.Schema); command.Parameters.AddWithValue("@table", table.Target.Name); command.Parameters.Add("@key", SqlDbType.VarBinary, -1).Value = SqlServerStableKeyCodec.Encode(key, table); await command.ExecuteNonQueryAsync(cancellationToken); } await transaction.CommitAsync(cancellationToken);
    }
    public async Task VerifyAsync(SqlServerExecutionContext context, CancellationToken cancellationToken)
    {
        const string sql = "(SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_affected_keys] WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_write_manifest] WHERE job_id=@job AND run_id=@run) UNION ALL (SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_write_manifest] WHERE job_id=@job AND run_id=@run EXCEPT SELECT table_schema,table_name,stable_key FROM [datapitcher].[transfer_affected_keys] WHERE job_id=@job AND run_id=@run)";
        await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); await using var command = new SqlCommand(sql, connection); command.Parameters.AddWithValue("@job", context.JobId); command.Parameters.AddWithValue("@run", context.RunId); await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Committed affected keys differ from the planned write manifest.");
    }
    private async Task<bool> ExistsAsync(string sql, string target, CancellationToken cancellationToken) { await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); await using var command = new SqlCommand(sql, connection); command.Parameters.AddWithValue("@target", target); return await command.ExecuteScalarAsync(cancellationToken) is not null; }
    private static async Task EnsureLedgerAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) { await using var command = new SqlCommand("IF SCHEMA_ID(N'datapitcher') IS NULL EXEC(N'CREATE SCHEMA [datapitcher]'); IF OBJECT_ID(N'[datapitcher].[transfer_affected_keys]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_affected_keys] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL,action_name nvarchar(6) NOT NULL); IF OBJECT_ID(N'[datapitcher].[transfer_write_manifest]',N'U') IS NULL CREATE TABLE [datapitcher].[transfer_write_manifest] (job_id uniqueidentifier NOT NULL,run_id uniqueidentifier NOT NULL,table_schema sysname NOT NULL,table_name sysname NOT NULL,stable_key varbinary(max) NOT NULL);", connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); }
}
public sealed class SqlServerIdentityRealigner(string targetConnectionString)
{
    public async Task RealignAsync(SqlServerWriteTable table, string column, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(targetConnectionString); await connection.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken); var target = SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name); const string identitySql = "SELECT ic.last_value,ic.increment_value FROM sys.identity_columns ic WHERE ic.object_id=OBJECT_ID(@target) AND ic.name=@column"; await using var identity = new SqlCommand(identitySql, connection, transaction); identity.Parameters.AddWithValue("@target", target); identity.Parameters.AddWithValue("@column", column); await using var identityRows = await identity.ExecuteReaderAsync(cancellationToken); if (!await identityRows.ReadAsync(cancellationToken)) { await transaction.CommitAsync(cancellationToken); return; } var current = identityRows.IsDBNull(0) ? (long?)null : Convert.ToInt64(identityRows.GetValue(0), CultureInfo.InvariantCulture); var increment = Convert.ToInt64(identityRows.GetValue(1), CultureInfo.InvariantCulture); await identityRows.CloseAsync(); var extremeSql = "SELECT " + (increment > 0 ? "MAX" : "MIN") + "(" + SqlServerIdentifier.Quote(column) + ") FROM " + target + " WITH (TABLOCKX,HOLDLOCK)"; await using var extreme = new SqlCommand(extremeSql, connection, transaction); if (await extreme.ExecuteScalarAsync(cancellationToken) is not object value || value is DBNull) { await transaction.CommitAsync(cancellationToken); return; } var bound = Convert.ToInt64(value, CultureInfo.InvariantCulture); var safe = current is not null && (increment > 0 ? current >= bound : current <= bound); if (safe) { await transaction.CommitAsync(cancellationToken); return; } await using var reseed = new SqlCommand("DBCC CHECKIDENT (N'" + target.Replace("'", "''", StringComparison.Ordinal) + "', RESEED, " + bound.ToString(CultureInfo.InvariantCulture) + ")", connection, transaction); await reseed.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
}
```

Preflight before sealing and first write blocks triggers and inbound cascades separately. Compare committed business keys only. Explicit identity inserts advance both directions, so lock, inspect the directional extreme, and reseed only when current is behind; cover non-identity, empty, clean, and empty-manifest cases.

- [ ] **Step 4: Run the StrictExact and identity tests and confirm they pass.**

Run: `./scripts/test-sqlserver.sh --filter "FullyQualifiedName~SqlServerStrictExactTests"`

Expected: `Passed!  - Failed:     0`; trigger and cascade refusal remain distinct, committed-set mismatch throws, and next identity values are 11 and -11 in the corresponding cases.

- [ ] **Step 5: Commit StrictExact verification and identity safety.**

Run: `git add src/DataPitcher.Providers.SqlServer/SqlServerStrictExact.cs tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerStrictExactTests.cs && git commit -m "feat: verify sqlserver transfer effects"`

## Self-Review

- [ ] Coverage: run `./scripts/test-sqlserver.sh` then `./scripts/test-all.sh`; require 100% line, branch, and method coverage.
- [ ] Covered: native fenced staging/apply, output capture, target recovery, keyset resume, StrictExact side-effect refusal/equality, and conditional identities.
- [ ] Deferred: materialization, control persistence, integrity scans, checksums, cycles, DirectFast StrictExact, remote targets, and unrelated-writer claims.
- [ ] Consistency checked: all `SqlServer*` types and method names above match, including xUnit 2.9.3-safe assertions.
