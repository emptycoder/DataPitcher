using DataPitcher.Core.Authorization;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using Xunit;
namespace DataPitcher.UnitTests.Selection;

public sealed class SelectionExecutionServiceTests
{
    [Fact]
    public void ExecutionModels_DefensivelyCopyCollections()
    {
        var parameters = new List<SelectionSqlParameter> { new("@p0", typeof(int), 7) };
        var sql = Sql(parameters); var raw = new RawSqlSelection(Orders, Orders.PrimaryKey!, "SELECT 7", parameters);
        var keys = new List<StableKey> { Key(7) }; var set = new SelectionKeySet(Orders, keys);
        var values = new Dictionary<string, SelectionPreviewCell> { ["id"] = new(7, false), ["ID"] = new(8, false) };
        var row = new SelectionPreviewRow(Key(7), values); var columns = new List<SelectionPreviewColumn> { new("id", true, false, false) }; var rows = new List<SelectionPreviewRow> { row };
        var preview = new SelectionPreview(columns, rows);

        parameters.Clear(); keys.Clear(); values.Clear(); columns.Clear(); rows.Clear();

        Assert.Single(sql.Parameters); Assert.False(sql.IsRawSql); Assert.Single(raw.Parameters); Assert.Single(set.Keys); Assert.Equal(Key(7), row.StableKey); Assert.Equal(2, row.Values.Count); Assert.Single(preview.Columns); Assert.Single(preview.Rows); Assert.Equal(100000, SelectionExecutionLimits.Default.MaximumResultSize);
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 0)]
    public void SelectionExecutionLimits_RejectsEachNonPositiveBound(int maximum, int validation, int keys, int preview, int count)
    {
        var limits = Limits(maximum, validation, keys, preview, count);
        Assert.Throws<ArgumentOutOfRangeException>(limits.Validate);
    }

    [Fact]
    public void SelectionExecutionRequest_RequiresExactlyOneMode()
    {
        var none = new SelectionExecutionRequest("schema", null, null, Limits());
        var both = new SelectionExecutionRequest("schema", Query(), new RawSqlSelection(Orders, Orders.PrimaryKey!, "SELECT 7", []), Limits());

        Assert.Equal("Selection execution requires exactly one query mode.", Assert.Throws<ArgumentException>(none.Validate).Message);
        Assert.Equal("Selection execution requires exactly one query mode.", Assert.Throws<ArgumentException>(both.Validate).Message);
    }

    [Fact]
    public void SelectionKeyAliases_ReturnsTransportNamesInStableKeyOrder()
    {
        var composite = new UniqueConstraint("PK_orders", ["tenant_id", "order_id"]);

        Assert.Equal("__datapitcher_key_0", SelectionKeyAliases.For(0)); Assert.Equal("__datapitcher_key_1", SelectionKeyAliases.ForKey(composite)[1]); Assert.Throws<ArgumentOutOfRangeException>(() => SelectionKeyAliases.For(-1));
    }

    [Fact]
    public void SelectionCountCache_StoresAndRetrievesAnExactKey()
    {
        var cache = new SelectionCountCache(); var key = SelectionCountCacheKey.Create("schema", Sql([]));

        Assert.False(cache.TryGet(key, out _)); cache.Set(key, 7); Assert.True(cache.TryGet(key, out var count)); Assert.Equal(7, count);
    }

    [Fact]
    public void SelectionCountCacheKey_ChangesForSchemaSqlTypedParameterAndStableKeyDefinition()
    {
        var first = SelectionCountCacheKey.Create("schema", Sql([new("@p0", typeof(int), 7)]));
        var schema = SelectionCountCacheKey.Create("changed", Sql([new("@p0", typeof(int), 7)]));
        var text = SelectionCountCacheKey.Create("schema", Sql([new("@p0", typeof(int), 7)], "SELECT changed"));
        var parameter = SelectionCountCacheKey.Create("schema", Sql([new("@p0", typeof(long), 7L)]));
        var keyOrder = SelectionCountCacheKey.Create("schema", Sql([], "SELECT 7", new UniqueConstraint("PK_orders", ["customer_id", "order_id"])));

        Assert.NotEqual(first, schema); Assert.NotEqual(first, text); Assert.NotEqual(first, parameter); Assert.NotEqual(first, keyOrder);
    }

    [Fact]
    public void SelectionExceptions_ExposeOperatorSafeMessages()
    {
        Assert.Equal("Selection result exceeds the maximum of 7 stable keys.", new SelectionResultLimitExceededException(7).Message);
        Assert.Equal("Selection preview timed out.", new SelectionOperationTimeoutException("preview").Message);
        Assert.Equal("invalid", new RawSqlValidationException("invalid").Message);
    }

    [Fact]
    public async Task CountAsync_CachesOnlyTheSameSchemaQueryParametersAndKeyDefinition()
    {
        var executor = new RecordingExecutor { Count = 2 }; var service = Service(executor); var request = AstRequest();

        Assert.Equal(2, await service.CountAsync(request, PermissionSet.Empty, CancellationToken.None)); Assert.Equal(2, await service.CountAsync(request, PermissionSet.Empty, CancellationToken.None)); Assert.Equal(1, executor.CountCalls);
        Assert.Equal(2, await service.CountAsync(request with { SchemaSnapshotHash = "changed" }, PermissionSet.Empty, CancellationToken.None)); Assert.Equal(2, executor.CountCalls);
    }

    [Fact]
    public async Task ExecuteKeysAsync_WhenJoinFansOut_ReturnsOnlyTheDeclaredRootKeySet()
    {
        var executor = new RecordingExecutor { Keys = new SelectionKeySet(Orders, [Key(7)]) };

        var result = await Service(executor).ExecuteKeysAsync(AstRequest(), PermissionSet.Empty, CancellationToken.None);

        Assert.Single(result.Keys, key => key == Key(7)); Assert.Equal("orders", result.RootTable.Name); Assert.Equal(100, executor.MaximumResultSize);
    }

    [Fact]
    public async Task ExecuteKeysAsync_WhenRawPermissionIsAbsent_ThrowsBeforeCallingTheProvider()
    {
        var executor = new RecordingExecutor();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(executor).ExecuteKeysAsync(RawRequest(), PermissionSet.Empty, CancellationToken.None));

        Assert.Equal("Missing permission: Selections.RawSql.", error.Message); Assert.Equal(0, executor.ValidationCalls);
    }

    [Fact]
    public async Task ExecuteKeysAsync_WhenRawPermissionIsPresent_MarksTheGeneratedSqlAsRaw()
    {
        var executor = new RecordingExecutor { Keys = new SelectionKeySet(Orders, [Key(7)]) };

        await Service(executor).ExecuteKeysAsync(RawRequest(), new PermissionSet([Permissions.SelectionsRawSql]), CancellationToken.None);

        Assert.True(executor.LastSelection!.IsRawSql);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("keys")]
    [InlineData("preview")]
    [InlineData("count")]
    public async Task Operations_WhenTheirBoundedOperationDoesNotComplete_ObserveTheNamedTimeout(string operation)
    {
        var executor = new BlockingExecutor(operation);

        var error = await Assert.ThrowsAsync<SelectionOperationTimeoutException>(() => InvokeAsync(Service(executor, TimeSpan.FromMilliseconds(20)), operation));

        Assert.Equal("Selection " + operation + " timed out.", error.Message); Assert.True(executor.Cancelled);
    }

    [Fact]
    public async Task PreviewAsync_UsesTheFixedServerLimitAndPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var executor = new RecordingExecutor();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(executor).PreviewAsync(AstRequest(), PermissionSet.Empty, cancellation.Token));

        Assert.Equal(SelectionExecutionLimits.PreviewRowLimit, executor.PreviewLimit); Assert.Equal(SelectionExecutionLimits.PreviewTextLength, executor.TextLimit); Assert.Equal(SelectionExecutionLimits.PreviewBinaryLength, executor.BinaryLimit); Assert.True(executor.ObservedCancellation);
    }

    [Fact]
    public async Task PreviewAsync_ReturnsTheProviderPreview()
    {
        var expected = new SelectionPreview([], []); var executor = new SuccessfulPreviewExecutor(expected);

        Assert.Equal(expected, await Service(executor).PreviewAsync(AstRequest(), PermissionSet.Empty, CancellationToken.None));
    }

    private static readonly TableDefinition Orders = new("sales", "orders", [new("order_id", typeof(int), false), new("customer_id", typeof(int), false)], new("PK_orders", ["order_id"]), []);
    private static StableKey Key(int id) => new([new("order_id", id)]);
    private static SelectionQuery Query() => new(new([Orders], []), new(Orders, "o"), new(Orders.PrimaryKey), [], null);
    private static SelectionExecutionRequest AstRequest() => new("schema", Query(), null, Limits());
    private static SelectionExecutionRequest RawRequest() => new("schema", null, new RawSqlSelection(Orders, Orders.PrimaryKey!, "SELECT @p0 AS __datapitcher_key_0", [new("@p0", typeof(int), 7)]), Limits());
    private static SelectionExecutionLimits Limits(int maximum = 100, int validation = 1, int keys = 1, int preview = 1, int count = 1) => new(maximum, TimeSpan.FromSeconds(validation), TimeSpan.FromSeconds(keys), TimeSpan.FromSeconds(preview), TimeSpan.FromSeconds(count));
    private static GeneratedSelectionSql Sql(IEnumerable<SelectionSqlParameter> parameters, string commandText = "SELECT @p0 AS __datapitcher_key_0", UniqueConstraint? stableKey = null) => new(commandText, Orders, stableKey ?? Orders.PrimaryKey!, parameters);
    private static SelectionExecutionService Service(ISelectionExecutor executor, TimeSpan? timeout = null) => new(new TestCompiler(), executor, new SelectionCountCache());
    private static async Task<long> InvokeAsync(SelectionExecutionService service, string operation) => operation switch { "validation" => await service.ExecuteKeysAsync(AstRequest(timeout: TimeSpan.FromMilliseconds(20)), PermissionSet.Empty, CancellationToken.None) is { } ? 0 : 0, "keys" => await service.ExecuteKeysAsync(AstRequest(timeout: TimeSpan.FromMilliseconds(20)), PermissionSet.Empty, CancellationToken.None) is { } ? 0 : 0, "preview" => (await service.PreviewAsync(AstRequest(timeout: TimeSpan.FromMilliseconds(20)), PermissionSet.Empty, CancellationToken.None)).Rows.Count, "count" => await service.CountAsync(AstRequest(timeout: TimeSpan.FromMilliseconds(20)), PermissionSet.Empty, CancellationToken.None), _ => throw new ArgumentOutOfRangeException(nameof(operation) ) };
    private static SelectionExecutionRequest AstRequest(TimeSpan timeout) => new("schema", Query(), null, new(100, timeout, timeout, timeout, timeout));

    private sealed class TestCompiler : ISelectionSqlCompiler
    {
        public GeneratedSelectionSql Compile(SelectionQuery query) => Sql([new("@p0", typeof(int), 7)]);
    }

    private class RecordingExecutor : ISelectionExecutor
    {
        public SelectionKeySet Keys { get; init; } = new(Orders, []);
        public long Count { get; init; }
        public int CountCalls { get; private set; }
        public int ValidationCalls { get; private set; }
        public int MaximumResultSize { get; private set; }
        public int PreviewLimit { get; private set; }
        public int TextLimit { get; private set; }
        public int BinaryLimit { get; private set; }
        public bool ObservedCancellation { get; private set; }
        public GeneratedSelectionSql? LastSelection { get; private set; }

        public virtual Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { LastSelection = selection; ValidationCalls++; return Task.CompletedTask; }
        public virtual Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken) { LastSelection = selection; MaximumResultSize = maximumResultSize; return Task.FromResult(Keys); }
        public virtual Task<SelectionPreview> PreviewAsync(GeneratedSelectionSql selection, int rowLimit, int textLimit, int binaryLimit, CancellationToken cancellationToken) { LastSelection = selection; PreviewLimit = rowLimit; TextLimit = textLimit; BinaryLimit = binaryLimit; ObservedCancellation = cancellationToken.IsCancellationRequested; return Task.FromCanceled<SelectionPreview>(cancellationToken); }
        public virtual Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) { LastSelection = selection; CountCalls++; return Task.FromResult(Count); }
    }

    private sealed class BlockingExecutor(string operation) : RecordingExecutor
    {
        public bool Cancelled { get; private set; }
        public override Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) => Block("validation", cancellationToken);
        public override Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken) => Block<SelectionKeySet>("keys", cancellationToken);
        public override Task<SelectionPreview> PreviewAsync(GeneratedSelectionSql selection, int rowLimit, int textLimit, int binaryLimit, CancellationToken cancellationToken) => Block<SelectionPreview>("preview", cancellationToken);
        public override Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) => Block<long>("count", cancellationToken);

        private Task Block(string current, CancellationToken cancellationToken) => current == operation ? ObserveCancellation(cancellationToken) : Task.CompletedTask;
        private Task<T> Block<T>(string current, CancellationToken cancellationToken) => current == operation ? ObserveCancellation<T>(cancellationToken) : Task.FromResult(default(T)!);
        private async Task ObserveCancellation(CancellationToken cancellationToken) { try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } catch (OperationCanceledException) { Cancelled = true; throw; } }
        private async Task<T> ObserveCancellation<T>(CancellationToken cancellationToken) { await ObserveCancellation(cancellationToken); return default!; }
    }

    private sealed class SuccessfulPreviewExecutor(SelectionPreview preview) : RecordingExecutor
    {
        public override Task<SelectionPreview> PreviewAsync(GeneratedSelectionSql selection, int rowLimit, int textLimit, int binaryLimit, CancellationToken cancellationToken) => Task.FromResult(preview);
    }
}
