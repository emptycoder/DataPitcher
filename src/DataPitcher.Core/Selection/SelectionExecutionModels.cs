using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Selection;

public enum RawSqlDialect
{
    PostgreSql,
    SqlServer,
}

public sealed class RawSqlSelection
{
    public RawSqlSelection(
        TableDefinition rootTable,
        UniqueConstraint rootStableKey,
        string commandText,
        IEnumerable<SelectionSqlParameter> parameters
    )
    {
        RootTable = rootTable;
        RootStableKey = rootStableKey;
        CommandText = commandText;
        Parameters = Array.AsReadOnly(parameters.ToArray());
    }

    public TableDefinition RootTable { get; }
    public UniqueConstraint RootStableKey { get; }
    public string CommandText { get; }
    public IReadOnlyList<SelectionSqlParameter> Parameters { get; }
}

public sealed record SelectionExecutionLimits(
    int MaximumResultSize,
    TimeSpan ValidationTimeout,
    TimeSpan KeyTimeout,
    TimeSpan PreviewTimeout,
    TimeSpan CountTimeout
)
{
    public const int PreviewRowLimit = 200;
    public const int PreviewTextLength = 256;
    public const int PreviewBinaryLength = 256;
    public static SelectionExecutionLimits Default { get; } =
        new(
            100000,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30)
        );

    public void Validate()
    {
        if (MaximumResultSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumResultSize));
        foreach (var value in new[] { ValidationTimeout, KeyTimeout, PreviewTimeout, CountTimeout })
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public sealed record SelectionExecutionRequest(
    string SchemaSnapshotHash,
    SelectionQuery? Query,
    RawSqlSelection? RawSql,
    SelectionExecutionLimits Limits
)
{
    public void Validate()
    {
        Limits.Validate();
        if ((Query is null) == (RawSql is null))
            throw new ArgumentException("Selection execution requires exactly one query mode.");
    }
}

public sealed class SelectionKeySet
{
    public SelectionKeySet(TableDefinition rootTable, IEnumerable<StableKey> keys)
    {
        RootTable = rootTable;
        Keys = Array.AsReadOnly(keys.ToArray());
    }

    public TableDefinition RootTable { get; }
    public IReadOnlyList<StableKey> Keys { get; }
}

public sealed record SelectionPreviewColumn(string Name, bool IsStableKey, bool IsForeignKey, bool IsGenerated);

public sealed record SelectionPreviewCell(object? Value, bool IsTruncated);

public sealed class SelectionPreviewRow
{
    public SelectionPreviewRow(StableKey stableKey, IReadOnlyDictionary<string, SelectionPreviewCell> values)
    {
        StableKey = stableKey;
        Values = new ReadOnlyDictionary<string, SelectionPreviewCell>(
            new Dictionary<string, SelectionPreviewCell>(values, StringComparer.Ordinal)
        );
    }

    public StableKey StableKey { get; }
    public IReadOnlyDictionary<string, SelectionPreviewCell> Values { get; }
}

public sealed class SelectionPreview
{
    public SelectionPreview(IEnumerable<SelectionPreviewColumn> columns, IEnumerable<SelectionPreviewRow> rows)
    {
        Columns = Array.AsReadOnly(columns.ToArray());
        Rows = Array.AsReadOnly(rows.ToArray());
    }

    public IReadOnlyList<SelectionPreviewColumn> Columns { get; }
    public IReadOnlyList<SelectionPreviewRow> Rows { get; }
}

public static class SelectionKeyAliases
{
    public static string For(int ordinal) =>
        ordinal < 0 ? throw new ArgumentOutOfRangeException(nameof(ordinal)) : "__datapitcher_key_" + ordinal;

    public static IReadOnlyList<string> ForKey(UniqueConstraint key) =>
        Array.AsReadOnly(Enumerable.Range(0, key.Columns.Count).Select(For).ToArray());
}

public sealed record SelectionCountCacheKey(
    string SchemaSnapshotHash,
    string NormalizedQueryHash,
    string ParameterHash,
    string StableKeyDefinition
)
{
    public static SelectionCountCacheKey Create(string schemaHash, GeneratedSelectionSql sql) =>
        new(
            schemaHash,
            Hash(sql.CommandText),
            Hash(
                string.Join(
                    "\u001f",
                    sql.Parameters.Select(parameter =>
                        parameter.Name
                        + "\u001e"
                        + parameter.ClrType.FullName
                        + "\u001e"
                        + System.Text.Json.JsonSerializer.Serialize(parameter.Value, parameter.ClrType)
                    )
                )
            ),
            string.Join("\u001f", sql.RootStableKey.Columns)
        );

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed class SelectionCountCache
{
    private readonly Dictionary<SelectionCountCacheKey, long> values = [];

    public bool TryGet(SelectionCountCacheKey key, out long count) => values.TryGetValue(key, out count);

    public void Set(SelectionCountCacheKey key, long count) => values[key] = count;
}

public sealed class SelectionResultLimitExceededException(int maximum)
    : InvalidOperationException("Selection result exceeds the maximum of " + maximum + " stable keys.");

public sealed class SelectionOperationTimeoutException(string operation)
    : TimeoutException("Selection " + operation + " timed out.");

public sealed class RawSqlValidationException(string message) : InvalidOperationException(message);

public interface ISelectionSqlCompiler
{
    GeneratedSelectionSql Compile(SelectionQuery query);
}

public interface ISelectionExecutor
{
    Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken);
    Task<SelectionKeySet> ReadKeysAsync(
        GeneratedSelectionSql selection,
        int maximumResultSize,
        CancellationToken cancellationToken
    );
    Task<SelectionPreview> PreviewAsync(
        GeneratedSelectionSql selection,
        int rowLimit,
        int textLimit,
        int binaryLimit,
        CancellationToken cancellationToken
    );
    Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken);
}
