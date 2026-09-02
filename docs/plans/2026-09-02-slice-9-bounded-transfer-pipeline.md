# DataPitcher Slice 9: Bounded Transfer Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a provider-independent, exhaustively unit-tested transfer pipeline that moves rows through conversion, bounded batching, and an abstract writer without unbounded memory growth.

**Architecture:** Core will expose source, conversion, and batch-writer seams, then compose them with a `Channel<TransferBatch>` whose capacity is strictly one or two batches and whose full mode waits. A batcher applies the sealed plan's existing `BatchTarget` to close batches on row count or payload bytes; the pipeline owns cancellation, backpressure, and accounting while providers later supply readers and native writers. No provider, database, JSON payload representation, or whole-transfer collection is needed to prove these pure properties.

**Tech Stack:** .NET 10, C# latest, `System.Threading.Channels`, async streams, xUnit 2.9.3, FsCheck.Xunit 3.3.2, Coverlet, ReportGenerator, Bash.

---

## File Structure

- `src/DataPitcher.Core/Transfer/TransferContracts.cs` — immutable row and batch values, pipeline configuration, and provider-free reader/converter/writer contracts.
- `src/DataPitcher.Core/Transfer/TransferBatcher.cs` — async-stream batch partitioner that enforces both batch limits.
- `src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs` — bounded channel producer-consumer execution, cancellation, and aggregate accounting.
- `src/DataPitcher.Core/Properties/AssemblyInfo.cs` — grants unit tests access to the internal deterministic backpressure probe constructor only.
- `tests/DataPitcher.UnitTests/Transfer/TransferContractsTests.cs` — contract immutability, validation, and invocation coverage.
- `tests/DataPitcher.UnitTests/Transfer/TransferBatcherTests.cs` — dual-limit, conversion, oversized-row, and FsCheck partition tests.
- `tests/DataPitcher.UnitTests/Transfer/BoundedTransferPipelineTests.cs` — deterministic backpressure and cancellation tests.
- `tests/DataPitcher.UnitTests/Transfer/TransferAccountingTests.cs` — row/byte totals, batch durations, rates, and inconsistent-writer tests.
- `docs/plans/2026-09-02-slice-9-bounded-transfer-pipeline.md` — this implementation plan.

## Scope and Deferrals

This slice is Core plus the existing unit-test project only. `DataPitcher.Core` must continue to reference no project or package: no ASP.NET, data access, SQL Server or PostgreSQL provider package. The existing architecture test enforces that boundary; no project-file change is required because `System.Threading.Channels` is part of the .NET runtime and FsCheck.Xunit is already present in the unit-test project.

Provider bulk writers, provider source readers, SQL, target transactions, target checkpoints, fence tokens, durable resume, and database access are deliberately deferred. ADR 0001 remains the authority for checkpoint/fencing work when a provider writer is built. This slice instead models the writer as an abstraction: backpressure, bounded memory, batch sizing, cancellation, and accounting are pure correctness properties that can be proved exhaustively and quickly without any database at all. All work belongs in `scripts/test-unit.sh`; it needs no Docker.

`BatchTarget` already belongs to the sealed transfer plan, so the pipeline will reuse it rather than invent a second configuration type. The pipeline options will admit only a one- or two-batch queue and an 8--32 MiB target payload size; a single row bigger than the byte target remains one indivisible batch rather than causing an unbounded accumulator. Batches never materialize a table or transfer, and source rows are never serialized through JSON. Do not replace the bounded channel with an unbounded queue, `ConcurrentQueue`, list, JSON transport, offset/paging collector, or whole-table/whole-transfer materialization.

Every new public member must be exercised by a test in the task that introduces it because the repository requires 100% line, branch, and method coverage after ReportGenerator merges all projects. `scripts/test-unit.sh` reports lane coverage but does not enforce it; only `scripts/test-all.sh` enforces all three 100% gates. Warnings are errors and promoted xUnit analyzer diagnostics are build failures. Prefer predicate overloads such as `Assert.Single(collection, predicate)` and `Assert.DoesNotContain(collection, predicate)` over LINQ-filtered assertions.

Timing-dependent tests are forbidden: a flaky test is a defect. Use `TaskCompletionSource` barriers to prove a producer has reached its third queue write while the writer holds the first batch, and use an injected clock function for durations and rates. Never use `Thread.Sleep`, `Task.Delay`, elapsed wall-clock assertions, or a retry loop to make a test pass.

### Task 1: Transfer contracts and immutable batch models

**Files:**
- Create: `src/DataPitcher.Core/Transfer/TransferContracts.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Transfer/TransferContractsTests.cs`

- [ ] **Step 1: Write the failing contract tests.**

```csharp
using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferContractsTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public void TransferRow_WhenSourceValuesChange_RetainsReadOnlyCopy()
    {
        object?[] values = [1, "before"]; var row = new TransferRow(values, 12); values[1] = "after";
        Assert.Equal("before", row.Values[1]); Assert.Equal(12, row.PayloadBytes);
        Assert.Throws<NotSupportedException>(() => ((IList<object?>)row.Values)[0] = 2);
    }
    [Theory]
    [InlineData(0, 8 * MiB)] [InlineData(1, 0)] [InlineData(1, 33 * MiB)] [InlineData(1, 8 * MiB, 3)]
    public void TransferPipelineOptions_WhenLimitsAreOutsideSupportedBounds_RejectsThem(int rows, int bytes, int queued = 1)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferPipelineOptions(new BatchTarget(rows, bytes), queued));
    }
    [Fact] public void TransferPipelineOptions_WhenLimitsAreValid_ExposesTheSealedTargetAndQueueBound()
    {
        var target = new BatchTarget(2, 8 * MiB); var options = new TransferPipelineOptions(target, 2);
        Assert.Equal(target, options.BatchTarget); Assert.Equal(2, options.MaximumQueuedBatches);
    }
    [Fact] public void TransferBatch_WhenConstructed_ExposesSequenceRowsAndPayload()
    {
        var batch = new TransferBatch(1, [new TransferRow([1], 4), new TransferRow([2], 6)]);
        Assert.Equal(1, batch.Sequence); Assert.Equal(2, batch.Rows.Count); Assert.Equal(10, batch.PayloadBytes);
    }
    [Fact] public void TransferValues_WhenBatchIsEmptyOrSequenceIsInvalid_RejectConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferBatch(0, [new TransferRow([1], 1)]));
        Assert.Throws<ArgumentException>(() => new TransferBatch(1, Array.Empty<TransferRow>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferRow([1], -1));
    }
    [Fact] public async Task TransferContracts_CanReadConvertAndWriteOneRow()
    {
        ITransferRowSource source = new SingleRowSource(new TransferRow([1], 4)); ITransferRowConverter converter = new IncrementingConverter(); ITransferBatchWriter writer = new CountingWriter();
        await foreach (var row in source.ReadAsync(CancellationToken.None))
        {
            var converted = await converter.ConvertAsync(row, CancellationToken.None);
            var result = await writer.WriteAsync(new TransferBatch(1, [converted]), CancellationToken.None);
            Assert.Equal(2, converted.Values[0]); Assert.Equal(1, result.Inserted); Assert.Equal(0, result.Updated); Assert.Equal(0, result.Skipped); Assert.Equal(0, result.Failed); Assert.Equal(4, result.BytesWritten);
        }
    }
    private sealed class SingleRowSource(TransferRow row) : ITransferRowSource
    {
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; }
    }
    private sealed class IncrementingConverter : ITransferRowConverter
    { public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) => ValueTask.FromResult(new TransferRow([(int)row.Values[0]! + 1], row.PayloadBytes)); }
    private sealed class CountingWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(new BatchWriteResult(batch.Rows.Count, 0, 0, 0, batch.PayloadBytes)); }
}
```

- [ ] **Step 2: Run the contract tests and confirm the missing namespace failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferContractsTests"`

Expected: compilation fails with CS0234, `The type or namespace name 'Transfer' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Write the minimal immutable models and contracts.**

```csharp
// src/DataPitcher.Core/Transfer/TransferContracts.cs
using DataPitcher.Core.Plans;
namespace DataPitcher.Core.Transfer;
public sealed class TransferRow
{
    public TransferRow(IEnumerable<object?> values, long payloadBytes)
    { ArgumentNullException.ThrowIfNull(values); ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes); Values = Array.AsReadOnly(values.ToArray()); PayloadBytes = payloadBytes; }
    public IReadOnlyList<object?> Values { get; }
    public long PayloadBytes { get; }
}
public sealed class TransferBatch
{
    public TransferBatch(long sequence, IEnumerable<TransferRow> rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1); ArgumentNullException.ThrowIfNull(rows);
        var copiedRows = rows.ToArray(); if (copiedRows.Length == 0) throw new ArgumentException("A transfer batch must contain at least one row.", nameof(rows));
        Sequence = sequence; Rows = Array.AsReadOnly(copiedRows); PayloadBytes = copiedRows.Sum(row => row.PayloadBytes);
    }
    public long Sequence { get; }
    public IReadOnlyList<TransferRow> Rows { get; }
    public long PayloadBytes { get; }
}
public sealed record BatchWriteResult(long Inserted, long Updated, long Skipped, long Failed, long BytesWritten);
public interface ITransferRowSource { IAsyncEnumerable<TransferRow> ReadAsync(CancellationToken cancellationToken); }
public interface ITransferRowConverter { ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken); }
public interface ITransferBatchWriter { Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken); }
public sealed class TransferPipelineOptions
{
    private const int MinimumPayloadBytes = 8 * 1024 * 1024;
    private const int MaximumPayloadBytes = 32 * 1024 * 1024;
    public TransferPipelineOptions(BatchTarget batchTarget, int maximumQueuedBatches)
    {
        ArgumentNullException.ThrowIfNull(batchTarget);
        if (batchTarget.MaximumRows < 1) throw new ArgumentOutOfRangeException(nameof(batchTarget), "Maximum rows must be positive.");
        if (batchTarget.MaximumBytes < MinimumPayloadBytes || batchTarget.MaximumBytes > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(batchTarget), "Target payload must be between 8 MiB and 32 MiB.");
        if (maximumQueuedBatches is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(maximumQueuedBatches), "The queue must contain one or two batches.");
        BatchTarget = batchTarget; MaximumQueuedBatches = maximumQueuedBatches;
    }
    public BatchTarget BatchTarget { get; }
    public int MaximumQueuedBatches { get; }
}
```

- [ ] **Step 4: Run the contract tests and confirm they pass.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferContractsTests"`

Expected: test run succeeds with `Failed: 0`.

- [ ] **Step 5: Commit the contract slice.**

Run: `git add src/DataPitcher.Core/Transfer/TransferContracts.cs tests/DataPitcher.UnitTests/Transfer/TransferContractsTests.cs && git commit -m "feat: add transfer pipeline contracts"`

### Task 2: Dual-limit batcher and exact partition property

**Files:**
- Create: `src/DataPitcher.Core/Transfer/TransferBatcher.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Transfer/TransferBatcherTests.cs`

- [ ] **Step 1: Write failing row-limit, byte-limit, converter, oversized-row, and partition-property tests.**

```csharp
using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using FsCheck.Xunit;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferBatcherTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task ReadBatchesAsync_WhenTinyRowsReachMaximumRows_ClosesOnRowLimit()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(3, 8 * MiB), 1)), [R(1, 1), R(2, 1), R(3, 1), R(4, 1)]);
        Assert.Equal([3, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal([1L, 2L], batches.Select(batch => batch.Sequence));
    }
    [Fact] public async Task ReadBatchesAsync_WhenWideRowsReachTargetPayload_ClosesOnByteLimit()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(10, 8 * MiB), 1)), [R(1, 4 * MiB), R(2, 4 * MiB), R(3, 5 * MiB), R(4, 5 * MiB)]);
        Assert.Equal([2, 1, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal(8L * MiB, batches[0].PayloadBytes);
    }
    [Fact] public async Task ReadBatchesAsync_WhenOneRowExceedsTarget_EmitsThatRowAlone()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(10, 8 * MiB), 1)), [R(1, 9 * MiB), R(2, 1)]);
        Assert.Equal([1, 1], batches.Select(batch => batch.Rows.Count)); Assert.Equal(9L * MiB, batches[0].PayloadBytes);
    }
    [Fact] public async Task ReadBatchesAsync_WhenConverterIsSupplied_WritesConvertedRows()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1)), [R(1, 1)], new IncrementingConverter());
        Assert.Equal(2, Assert.Single(batches).Rows[0].Values[0]);
    }
    [Fact] public async Task ReadBatchesAsync_WhenSourceIsEmpty_EmitsNoBatch()
    {
        var batches = await BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1)), []);
        Assert.Empty(batches);
    }
    [Property(MaxTest = 100)] public void ReadBatchesAsync_PartitionsEveryGeneratedInputExactlyOnce(byte[] payloads)
    {
        var rows = payloads.Select((payload, index) => R(index, payload + 1L)).ToArray();
        var batches = BatchesAsync(new TransferBatcher(new TransferPipelineOptions(new BatchTarget(3, 8 * MiB), 1)), rows).GetAwaiter().GetResult();
        var flattened = batches.SelectMany(batch => batch.Rows).Select(row => (int)row.Values[0]!).ToArray();
        Assert.Equal(Enumerable.Range(0, rows.Length), flattened); Assert.Equal(flattened.Length, flattened.Distinct().Count());
    }
    private static TransferRow R(int value, long bytes) => new TransferRow([value], bytes);
    private static async Task<List<TransferBatch>> BatchesAsync(TransferBatcher batcher, IEnumerable<TransferRow> rows, ITransferRowConverter? converter = null)
    { var result = new List<TransferBatch>(); await foreach (var batch in batcher.ReadBatchesAsync(new Source(rows), converter, CancellationToken.None)) result.Add(batch); return result; }
    private sealed class Source : ITransferRowSource
    {
        private readonly IReadOnlyList<TransferRow> _rows; public Source(IEnumerable<TransferRow> rows) => _rows = rows.ToArray();
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { foreach (var row in _rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; } }
    }
    private sealed class IncrementingConverter : ITransferRowConverter
    { public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) => ValueTask.FromResult(new TransferRow([(int)row.Values[0]! + 1], row.PayloadBytes)); }
}
```

- [ ] **Step 2: Run the batcher tests and confirm the type is missing.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferBatcherTests"`

Expected: compilation fails with CS0246, `The type or namespace name 'TransferBatcher' could not be found`.

- [ ] **Step 3: Write the streaming batcher.**

```csharp
// src/DataPitcher.Core/Transfer/TransferBatcher.cs
using System.Runtime.CompilerServices;
namespace DataPitcher.Core.Transfer;
public sealed class TransferBatcher
{
    private readonly TransferPipelineOptions _options;
    public TransferBatcher(TransferPipelineOptions options) { ArgumentNullException.ThrowIfNull(options); _options = options; }
    public async IAsyncEnumerable<TransferBatch> ReadBatchesAsync(ITransferRowSource source, ITransferRowConverter? converter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); var rows = new List<TransferRow>(); long bytes = 0; long sequence = 1;
        await foreach (var sourceRow in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested(); var row = converter is null ? sourceRow : await converter.ConvertAsync(sourceRow, cancellationToken);
            if (rows.Count > 0 && row.PayloadBytes > _options.BatchTarget.MaximumBytes - bytes)
            { yield return new TransferBatch(sequence++, rows); rows = []; bytes = 0; }
            rows.Add(row); bytes += row.PayloadBytes;
            if (rows.Count == _options.BatchTarget.MaximumRows || bytes >= _options.BatchTarget.MaximumBytes)
            { yield return new TransferBatch(sequence++, rows); rows = []; bytes = 0; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count > 0) yield return new TransferBatch(sequence, rows);
    }
}
```

- [ ] **Step 4: Run the batcher tests and confirm every partition property passes.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferBatcherTests"`

Expected: test run succeeds with `Failed: 0`; FsCheck reports 100 successful generated partition cases. The tests establish non-overlap and that the ordered union of batches equals the input exactly, so no row is lost or duplicated.

- [ ] **Step 5: Commit the batcher slice.**

Run: `git add src/DataPitcher.Core/Transfer/TransferBatcher.cs tests/DataPitcher.UnitTests/Transfer/TransferBatcherTests.cs && git commit -m "feat: batch transfer rows by count and payload"`

### Task 3: Bounded channel execution, backpressure, and cancellation

**Files:**
- Create: `src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs`, `src/DataPitcher.Core/Properties/AssemblyInfo.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Transfer/BoundedTransferPipelineTests.cs`

- [ ] **Step 1: Write failing deterministic backpressure and cancellation tests.**

```csharp
using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class BoundedTransferPipelineTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task RunAsync_WhenWriterIsBlocked_ThrottlesFastSourceAtFixedRowBound()
    {
        var options = new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1); var source = new CountingSource(8); var writer = new GateWriter(); var thirdEnqueue = new TaskCompletionSource<TransferBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new BoundedTransferPipeline(options, () => DateTimeOffset.UnixEpoch, batch => { if (batch.Sequence == 3) thirdEnqueue.TrySetResult(batch); });
        var run = pipeline.RunAsync(source, null, writer, CancellationToken.None);
        await writer.FirstWriteStarted.Task; await thirdEnqueue.Task;
        Assert.Equal(6, source.RowsRead);
        Assert.InRange(source.RowsRead, 0, (options.MaximumQueuedBatches + 2) * options.BatchTarget.MaximumRows);
        writer.ReleaseFirstWrite(); await run; Assert.Equal([1L, 2L, 3L, 4L], writer.Batches.Select(batch => batch.Sequence));
    }
    [Fact] public async Task RunAsync_WhenCancelledWithAnIncompleteBatch_DoesNotWriteThatPartialBatch()
    {
        using var cancellation = new CancellationTokenSource(); var source = new PausingSource(); var converter = new RecordingConverter(); var writer = new GateWriter();
        var run = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(4, 8 * MiB), 1), () => DateTimeOffset.UnixEpoch).RunAsync(source, converter, writer, cancellation.Token);
        await source.FirstRowRead.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(source.ReceivedCancelableToken); Assert.True(converter.ReceivedCancelableToken); Assert.Empty(writer.Batches);
    }
    [Fact] public async Task RunAsync_WhenCancelledDuringWriter_PropagatesCancellationToEveryActiveStage()
    {
        using var cancellation = new CancellationTokenSource(); var source = new CountingSource(2); var converter = new RecordingConverter(); var writer = new GateWriter();
        var run = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), () => DateTimeOffset.UnixEpoch).RunAsync(source, converter, writer, cancellation.Token);
        await writer.FirstWriteStarted.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(source.ReceivedCancelableToken); Assert.True(converter.ReceivedCancelableToken); Assert.True(writer.ReceivedCancelableToken); Assert.True(writer.ObservedCancellation);
    }
    private sealed class CountingSource(int count) : ITransferRowSource
    {
        public int RowsRead { get; private set; } public bool ReceivedCancelableToken { get; private set; }
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { ReceivedCancelableToken = cancellationToken.CanBeCanceled; for (var value = 1; value <= count; value++) { cancellationToken.ThrowIfCancellationRequested(); RowsRead++; yield return new TransferRow([value], 1); await Task.CompletedTask; } }
    }
    private sealed class PausingSource : ITransferRowSource
    {
        public TaskCompletionSource<bool> FirstRowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool ReceivedCancelableToken { get; private set; }
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { ReceivedCancelableToken = cancellationToken.CanBeCanceled; FirstRowRead.TrySetResult(true); yield return new TransferRow([1], 1); await new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken); }
    }
    private sealed class RecordingConverter : ITransferRowConverter
    { public bool ReceivedCancelableToken { get; private set; } public ValueTask<TransferRow> ConvertAsync(TransferRow row, CancellationToken cancellationToken) { ReceivedCancelableToken = cancellationToken.CanBeCanceled; return ValueTask.FromResult(row); } }
    private sealed class GateWriter : ITransferBatchWriter
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously); public List<TransferBatch> Batches { get; } = []; public TaskCompletionSource<bool> FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool ReceivedCancelableToken { get; private set; } public bool ObservedCancellation { get; private set; }
        public async Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken)
        { Batches.Add(batch); ReceivedCancelableToken = cancellationToken.CanBeCanceled; if (batch.Sequence == 1) { FirstWriteStarted.TrySetResult(true); try { await _release.Task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { ObservedCancellation = true; throw; } } return new(batch.Rows.Count, 0, 0, 0, batch.PayloadBytes); }
        public void ReleaseFirstWrite() => _release.TrySetResult(true);
    }
}
```

- [ ] **Step 2: Run the pipeline tests and confirm the execution type is absent.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~BoundedTransferPipelineTests"`

Expected: compilation fails with CS0246, `The type or namespace name 'BoundedTransferPipeline' could not be found`.

- [ ] **Step 3: Write the bounded producer-consumer pipeline and its test-only internal constructor.**

```csharp
// src/DataPitcher.Core/Properties/AssemblyInfo.cs
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("DataPitcher.UnitTests")]

// src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs
using System.Threading.Channels;
namespace DataPitcher.Core.Transfer;
public sealed class BoundedTransferPipeline
{
    private readonly TransferBatcher _batcher; private readonly Action<TransferBatch>? _beforeQueueWrite;
    public BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow) : this(options, utcNow, null) { }
    internal BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow, Action<TransferBatch>? beforeQueueWrite)
    { ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(utcNow); _batcher = new TransferBatcher(options); _beforeQueueWrite = beforeQueueWrite; MaximumQueuedBatches = options.MaximumQueuedBatches; }
    internal int MaximumQueuedBatches { get; }
    public async Task RunAsync(ITransferRowSource source, ITransferRowConverter? converter, ITransferBatchWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(writer);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<TransferBatch>(new BoundedChannelOptions(MaximumQueuedBatches) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(channel.Writer, source, converter, stopped.Token);
        try { await ConsumeAsync(channel.Reader, writer, stopped.Token); await producer; }
        catch { stopped.Cancel(); try { await producer; } catch { } throw; }
    }
    private async Task ProduceAsync(ChannelWriter<TransferBatch> writer, ITransferRowSource source, ITransferRowConverter? converter, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await foreach (var batch in _batcher.ReadBatchesAsync(source, converter, cancellationToken)) { _beforeQueueWrite?.Invoke(batch); await writer.WriteAsync(batch, cancellationToken); } }
        catch (Exception exception) { failure = exception; throw; }
        finally { writer.TryComplete(failure); }
    }
    private static async Task ConsumeAsync(ChannelReader<TransferBatch> reader, ITransferBatchWriter writer, CancellationToken cancellationToken)
    { await foreach (var batch in reader.ReadAllAsync(cancellationToken)) await writer.WriteAsync(batch, cancellationToken); }
}
```

The deterministic third-enqueue barrier proves the producer has assembled a third full batch while batch one is held by the writer and batch two occupies the only queue slot. It therefore cannot read a seventh row: including the writer's in-flight batch, the queue, and the producer's current batch, the fixed bound is `(MaximumQueuedBatches + 2) * MaximumRows` for the tiny rows in this test. The channel itself contains no more than `MaximumQueuedBatches` batches; byte limits independently bound each normal batch. Cancellation cancels the linked token passed to source, converter, blocked queue write, and writer, then prevents the incomplete accumulator from being yielded.

- [ ] **Step 4: Run the pipeline tests and confirm deterministic backpressure and cancellation pass.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~BoundedTransferPipelineTests"`

Expected: test run succeeds with `Failed: 0`; no sleep or elapsed-time assertion is present.

- [ ] **Step 5: Commit the bounded-execution slice.**

Run: `git add src/DataPitcher.Core/Properties/AssemblyInfo.cs src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs tests/DataPitcher.UnitTests/Transfer/BoundedTransferPipelineTests.cs && git commit -m "feat: add bounded transfer execution"`

### Task 4: Transfer accounting and deterministic rates

**Files:**
- Create: none
- Modify: `src/DataPitcher.Core/Transfer/TransferContracts.cs`, `src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs`
- Test: `tests/DataPitcher.UnitTests/Transfer/TransferAccountingTests.cs`

- [ ] **Step 1: Write failing accounting and invalid-writer tests.**

```csharp
using System.Runtime.CompilerServices;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;
namespace DataPitcher.UnitTests.Transfer;
public sealed class TransferAccountingTests
{
    private const int MiB = 1024 * 1024;
    [Fact] public async Task RunAsync_AccountsRowsBytesDurationsAndRates()
    {
        var clock = new ManualClock([At(0), At(0), At(2), At(2), At(5), At(5)]);
        var pipeline = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), clock.Next);
        var result = await pipeline.RunAsync(new Source([R(1), R(2), R(3), R(4)]), null, new AccountingWriter(), CancellationToken.None);
        Assert.Equal(4, result.RowsRead); Assert.Equal(1, result.RowsInserted); Assert.Equal(1, result.RowsUpdated); Assert.Equal(1, result.RowsSkipped); Assert.Equal(1, result.RowsFailed);
        Assert.Equal(16L * MiB, result.BytesRead); Assert.Equal(16L * MiB, result.BytesWritten); Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        Assert.Equal(2, result.Batches[0].Batch.Rows.Count); Assert.Equal(1, result.Batches[0].Result.Inserted); Assert.Equal(TimeSpan.FromSeconds(2), result.Batches[0].Duration); Assert.Equal(TimeSpan.FromSeconds(3), result.Batches[1].Duration);
        Assert.Equal(0.8d, result.RowsPerSecond, 10); Assert.Equal(3.2d, result.MebibytesPerSecond, 10);
    }
    [Fact] public async Task RunAsync_WhenWriterDoesNotAccountForEveryRow_RejectsTheResult()
    {
        var pipeline = new BoundedTransferPipeline(new TransferPipelineOptions(new BatchTarget(2, 8 * MiB), 1), () => At(0));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(new Source([R(1), R(2)]), null, new IncorrectWriter(), CancellationToken.None));
        Assert.Equal("Writer result must account for every row in batch 1.", exception.Message);
    }
    [Fact] public void TransferPipelineResult_WhenDurationIsZero_ReportsZeroRates()
    {
        var result = new TransferPipelineResult(1, 1, [], TimeSpan.Zero);
        Assert.Equal(0, result.RowsPerSecond); Assert.Equal(0, result.MebibytesPerSecond); Assert.Empty(result.Batches);
    }
    private static TransferRow R(int value) => new([value], 4L * MiB);
    private static DateTimeOffset At(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    private sealed class Source(IReadOnlyList<TransferRow> rows) : ITransferRowSource
    { public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken) { foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; await Task.CompletedTask; } } }
    private sealed class AccountingWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(batch.Sequence == 1 ? new BatchWriteResult(1, 1, 0, 0, batch.PayloadBytes) : new BatchWriteResult(0, 0, 1, 1, batch.PayloadBytes)); }
    private sealed class IncorrectWriter : ITransferBatchWriter
    { public Task<BatchWriteResult> WriteAsync(TransferBatch batch, CancellationToken cancellationToken) => Task.FromResult(new BatchWriteResult(1, 0, 0, 0, batch.PayloadBytes)); }
    private sealed class ManualClock(IEnumerable<DateTimeOffset> values)
    { private readonly Queue<DateTimeOffset> _values = new Queue<DateTimeOffset>(values); public DateTimeOffset Next() => _values.Dequeue(); }
}
```

- [ ] **Step 2: Run the accounting tests and confirm the missing result-model failure.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferAccountingTests"`

Expected: compilation fails with CS0246, `The type or namespace name 'TransferPipelineResult' could not be found`.

- [ ] **Step 3: Add result models and evolve the pipeline to measure the injected clock.**

```csharp
// Append to src/DataPitcher.Core/Transfer/TransferContracts.cs
public sealed class BatchTransferStatistics
{
    public BatchTransferStatistics(TransferBatch batch, BatchWriteResult result, TimeSpan duration) { Batch = batch; Result = result; Duration = duration; }
    public TransferBatch Batch { get; } public BatchWriteResult Result { get; } public TimeSpan Duration { get; }
}
public sealed class TransferPipelineResult
{
    public TransferPipelineResult(long rowsRead, long bytesRead, IEnumerable<BatchTransferStatistics> batches, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(batches); RowsRead = rowsRead; BytesRead = bytesRead; Batches = Array.AsReadOnly(batches.ToArray()); Duration = duration;
        RowsInserted = Batches.Sum(batch => batch.Result.Inserted); RowsUpdated = Batches.Sum(batch => batch.Result.Updated); RowsSkipped = Batches.Sum(batch => batch.Result.Skipped); RowsFailed = Batches.Sum(batch => batch.Result.Failed); BytesWritten = Batches.Sum(batch => batch.Result.BytesWritten);
    }
    public long RowsRead { get; } public long RowsInserted { get; } public long RowsUpdated { get; } public long RowsSkipped { get; } public long RowsFailed { get; }
    public long BytesRead { get; } public long BytesWritten { get; } public IReadOnlyList<BatchTransferStatistics> Batches { get; } public TimeSpan Duration { get; }
    public double RowsPerSecond => Duration <= TimeSpan.Zero ? 0 : RowsRead / Duration.TotalSeconds;
    public double MebibytesPerSecond => Duration <= TimeSpan.Zero ? 0 : BytesWritten / 1024d / 1024d / Duration.TotalSeconds;
}

// Replace src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
namespace DataPitcher.Core.Transfer;
public sealed class BoundedTransferPipeline
{
    private readonly TransferBatcher _batcher; private readonly Func<DateTimeOffset> _utcNow; private readonly Action<TransferBatch>? _beforeQueueWrite;
    public BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow) : this(options, utcNow, null) { }
    internal BoundedTransferPipeline(TransferPipelineOptions options, Func<DateTimeOffset> utcNow, Action<TransferBatch>? beforeQueueWrite)
    { ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(utcNow); _batcher = new TransferBatcher(options); _utcNow = utcNow; _beforeQueueWrite = beforeQueueWrite; MaximumQueuedBatches = options.MaximumQueuedBatches; }
    internal int MaximumQueuedBatches { get; }
    public async Task<TransferPipelineResult> RunAsync(ITransferRowSource source, ITransferRowConverter? converter, ITransferBatchWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(writer); var startedAt = _utcNow(); long rowsRead = 0; long bytesRead = 0; var batches = new List<BatchTransferStatistics>();
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<TransferBatch>(new BoundedChannelOptions(MaximumQueuedBatches) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(channel.Writer, new CountingSource(source, row => { rowsRead++; bytesRead += row.PayloadBytes; }), converter, stopped.Token);
        try { await ConsumeAsync(channel.Reader, writer, batches, stopped.Token); await producer; return new TransferPipelineResult(rowsRead, bytesRead, batches, _utcNow() - startedAt); }
        catch { stopped.Cancel(); try { await producer; } catch { } throw; }
    }
    private async Task ProduceAsync(ChannelWriter<TransferBatch> writer, ITransferRowSource source, ITransferRowConverter? converter, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await foreach (var batch in _batcher.ReadBatchesAsync(source, converter, cancellationToken)) { _beforeQueueWrite?.Invoke(batch); await writer.WriteAsync(batch, cancellationToken); } }
        catch (Exception exception) { failure = exception; throw; }
        finally { writer.TryComplete(failure); }
    }
    private async Task ConsumeAsync(ChannelReader<TransferBatch> reader, ITransferBatchWriter writer, ICollection<BatchTransferStatistics> batches, CancellationToken cancellationToken)
    {
        await foreach (var batch in reader.ReadAllAsync(cancellationToken))
        {
            var startedAt = _utcNow(); var result = await writer.WriteAsync(batch, cancellationToken); var finishedAt = _utcNow();
            if (result.Inserted + result.Updated + result.Skipped + result.Failed != batch.Rows.Count) throw new InvalidOperationException($"Writer result must account for every row in batch {batch.Sequence}.");
            batches.Add(new BatchTransferStatistics(batch, result, finishedAt - startedAt));
        }
    }
    private sealed class CountingSource(ITransferRowSource source, Action<TransferRow> rowRead) : ITransferRowSource
    {
        public async IAsyncEnumerable<TransferRow> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var row in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken)) { rowRead(row); yield return row; } }
    }
}
```

The source accounting wrapper observes a row before optional conversion, so `BytesRead` reports source payload while each writer result reports target bytes. Each writer invocation is timed with the injected clock, and the complete result exposes inserted, updated, skipped, and failed rows, bytes read/written, per-batch duration, and deterministic rows-per-second and MiB-per-second rates. Rejecting a result whose outcome count differs from the batch row count prevents a provider implementation from silently corrupting aggregate accounting.

- [ ] **Step 4: Run the accounting tests, then the required unit and merged-coverage lanes.**

Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~TransferAccountingTests" && scripts/test-unit.sh && scripts/test-all.sh`

Expected: the focused accounting test and the unit lane succeed with `Failed: 0`; `scripts/test-all.sh` prints `Merged coverage: line=100% branch=100% method=100%`. The latter is the only coverage gate and also confirms no unexercised branch or public member remains.

- [ ] **Step 5: Commit the accounting slice.**

Run: `git add src/DataPitcher.Core/Transfer/TransferContracts.cs src/DataPitcher.Core/Transfer/BoundedTransferPipeline.cs tests/DataPitcher.UnitTests/Transfer/TransferAccountingTests.cs && git commit -m "feat: account for bounded transfer batches"`

## Self-Review

- [ ] Confirmed coverage: Tasks 1--4 define every later-used type before use; exercise source, optional conversion, abstract writer, immutable batches, queue capacity, row/byte limits, one oversized row, exact FsCheck partitioning, deterministic backpressure, prompt cancellation with no partial emission, per-status row totals, bytes, batch durations, and rates.
- [ ] Confirmed deferrals: no provider bulk writer, database read/write, SQL, JSON payload transport, checkpoint, transaction, target fencing, resume, table materialization, or transfer materialization is introduced. The bounded writer seam leaves provider-native implementations to later slices while ADR 0001 continues to govern their checkpoint transaction.
- [ ] Confirmed type and method-name consistency across tasks: `TransferRow`, `TransferBatch`, `BatchWriteResult`, `TransferPipelineOptions`, `TransferBatcher.ReadBatchesAsync`, `BoundedTransferPipeline.RunAsync`, `BatchTransferStatistics`, and `TransferPipelineResult` use the same namespaces, constructor parameters, and property names in every earlier definition and later test.
- [ ] Before accepting implementation, rerun `scripts/test-unit.sh` without a filter and `scripts/test-all.sh`; reject the change if either build has a warning, any analyzer diagnostic, a timing-dependent test, a non-100% merged coverage metric, or a Core project/package reference.
