using DataPitcher.Core.Authorization;
namespace DataPitcher.Core.Selection;

public sealed class SelectionExecutionService(ISelectionSqlCompiler compiler, ISelectionExecutor executor, SelectionCountCache counts)
{
    public async Task<SelectionKeySet> ExecuteKeysAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken)
    {
        var sql = await PrepareAsync(request, permissions, cancellationToken);
        return await BoundedAsync("keys", request.Limits.KeyTimeout, token => executor.ReadKeysAsync(sql, request.Limits.MaximumResultSize, token), cancellationToken);
    }

    public async Task<SelectionPreview> PreviewAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken)
    {
        var sql = await PrepareAsync(request, permissions, cancellationToken);
        return await BoundedAsync("preview", request.Limits.PreviewTimeout, token => executor.PreviewAsync(sql, SelectionExecutionLimits.PreviewRowLimit, SelectionExecutionLimits.PreviewTextLength, SelectionExecutionLimits.PreviewBinaryLength, token), cancellationToken);
    }

    public async Task<long> CountAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken)
    {
        var sql = await PrepareAsync(request, permissions, cancellationToken);
        var key = SelectionCountCacheKey.Create(request.SchemaSnapshotHash, sql);
        if (counts.TryGet(key, out var cached)) return cached;
        var count = await BoundedAsync("count", request.Limits.CountTimeout, token => executor.CountAsync(sql, token), cancellationToken);
        counts.Set(key, count);
        return count;
    }

    private async Task<GeneratedSelectionSql> PrepareAsync(SelectionExecutionRequest request, PermissionSet permissions, CancellationToken cancellationToken)
    {
        request.Validate();
        var sql = request.RawSql is { } raw
            ? permissions.Contains(Permissions.SelectionsRawSql)
                ? new GeneratedSelectionSql(raw.CommandText, raw.RootTable, raw.RootStableKey, raw.Parameters, true)
                : throw new UnauthorizedAccessException("Missing permission: Selections.RawSql.")
            : compiler.Compile(request.Query!);
        await BoundedAsync("validation", request.Limits.ValidationTimeout, async token => { await executor.ValidateAsync(sql, token); return true; }, cancellationToken);
        return sql;
    }

    private static async Task<T> BoundedAsync<T>(string operation, TimeSpan timeout, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try { return await action(linked.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SelectionOperationTimeoutException(operation); }
    }
}
