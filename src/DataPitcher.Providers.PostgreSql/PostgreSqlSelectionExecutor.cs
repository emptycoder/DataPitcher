using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlSelectionExecutor(NpgsqlDataSource source, PostgreSqlSchemaSnapshot schema) : ISelectionExecutor
{
    public async Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken)
    {
        if (selection.IsRawSql) RawSqlSafetyValidator.Validate(RawSqlDialect.PostgreSql, selection.CommandText);
        await using var command = Command("/* DataPitcher.Selection.Validate */ SELECT * FROM (" + selection.CommandText + ") AS selection LIMIT 1", selection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        RequireAliases(reader, selection.RootStableKey);
    }

    public async Task<SelectionKeySet> ReadKeysAsync(GeneratedSelectionSql selection, int maximumResultSize, CancellationToken cancellationToken)
    {
        var aliases = Aliases(selection.RootStableKey);
        await using var command = Command("/* DataPitcher.Selection.Keys */ SELECT DISTINCT " + aliases + " FROM (" + selection.CommandText + ") AS selection ORDER BY " + aliases + " LIMIT @take", selection);
        command.Parameters.AddWithValue("take", checked(maximumResultSize + 1));
        var keys = await ReadKeysAsync(command, selection.RootStableKey, cancellationToken);
        if (keys.Count > maximumResultSize) throw new SelectionResultLimitExceededException(maximumResultSize);
        return new SelectionKeySet(selection.RootTable, keys);
    }

    public async Task<SelectionPreview> PreviewAsync(GeneratedSelectionSql selection, int rowLimit, int textLimit, int binaryLimit, CancellationToken cancellationToken)
    {
        var root = schema.Table(selection.RootTable.Name).Definition;
        var aliases = Aliases(selection.RootStableKey);
        var rootAlias = PostgreSqlIdentifier.Quote("root");
        var keys = SelectionKeyAliases.ForKey(selection.RootStableKey);
        var projection = string.Join(", ", keys.Select(PostgreSqlIdentifier.Quote).Concat(root.Columns.Select(column => PreviewProjection(rootAlias, column))));
        var join = string.Join(" AND ", selection.RootStableKey.Columns.Select((column, index) => rootAlias + "." + PostgreSqlIdentifier.Quote(column) + " = keys." + PostgreSqlIdentifier.Quote(keys[index])));
        var order = string.Join(", ", selection.RootStableKey.Columns.Select(column => rootAlias + "." + PostgreSqlIdentifier.Quote(column)));
        var sql = "/* DataPitcher.Selection.Preview */ WITH selection AS (" + selection.CommandText + "), keys AS (SELECT DISTINCT " + aliases + " FROM selection ORDER BY " + aliases + " LIMIT @previewLimit) SELECT " + projection + " FROM " + PostgreSqlIdentifier.Qualified(root.Schema, root.Name) + " AS " + rootAlias + " INNER JOIN keys ON " + join + " ORDER BY " + order;
        await using var command = Command(sql, selection);
        command.Parameters.AddWithValue("previewLimit", rowLimit);
        command.Parameters.AddWithValue("textLimit", textLimit);
        command.Parameters.AddWithValue("binaryLimit", binaryLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadPreviewAsync(reader, root, selection.RootStableKey, cancellationToken);
    }

    public async Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken)
    {
        var aliases = Aliases(selection.RootStableKey);
        await using var command = Command("/* DataPitcher.Selection.Count */ SELECT count(*) FROM (SELECT DISTINCT " + aliases + " FROM (" + selection.CommandText + ") AS selection) AS keys", selection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private NpgsqlCommand Command(string sql, GeneratedSelectionSql selection)
    {
        var command = source.CreateCommand(sql);
        foreach (var parameter in selection.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private static async Task<List<StableKey>> ReadKeysAsync(NpgsqlCommand command, UniqueConstraint stableKey, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<StableKey>();
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(new StableKey(stableKey.Columns.Select((column, index) => new KeyComponent(column, reader.GetValue(index)))));
        return keys;
    }

    private async Task<SelectionPreview> ReadPreviewAsync(NpgsqlDataReader reader, TableDefinition root, UniqueConstraint stableKey, CancellationToken cancellationToken)
    {
        var columns = root.Columns.Select(column => new SelectionPreviewColumn(
            column.Name,
            stableKey.Columns.Contains(column.Name, StringComparer.Ordinal),
            schema.ForeignKeys.Any(foreignKey => string.Equals(foreignKey.ChildTable.Schema, root.Schema, StringComparison.Ordinal) && string.Equals(foreignKey.ChildTable.Name, root.Name, StringComparison.Ordinal) && foreignKey.ChildColumns.Contains(column.Name, StringComparer.Ordinal)),
            column.IsGenerated)).ToArray();
        var rows = new List<SelectionPreviewRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new StableKey(stableKey.Columns.Select((column, index) => new KeyComponent(column, reader.GetValue(index))));
            var values = new Dictionary<string, SelectionPreviewCell>(StringComparer.Ordinal);
            for (var index = 0; index < root.Columns.Count; index++)
            {
                var offset = stableKey.Columns.Count + (index * 2);
                values.Add(root.Columns[index].Name, new SelectionPreviewCell(reader.IsDBNull(offset) ? null : reader.GetValue(offset), reader.GetBoolean(offset + 1)));
            }
            rows.Add(new SelectionPreviewRow(key, values));
        }
        return new SelectionPreview(columns, rows);
    }

    private static string Aliases(UniqueConstraint stableKey) => string.Join(", ", SelectionKeyAliases.ForKey(stableKey).Select(PostgreSqlIdentifier.Quote));

    private static string PreviewProjection(string rootAlias, ColumnDefinition column)
    {
        var value = rootAlias + "." + PostgreSqlIdentifier.Quote(column.Name);
        return column.ClrType == typeof(string)
            ? "CASE WHEN length(" + value + ") > @textLimit THEN left(" + value + ", @textLimit) ELSE " + value + " END, COALESCE(length(" + value + ") > @textLimit, false)"
            : column.ClrType == typeof(byte[])
                ? "CASE WHEN octet_length(" + value + ") > @binaryLimit THEN substring(" + value + " FROM 1 FOR @binaryLimit) ELSE " + value + " END, COALESCE(octet_length(" + value + ") > @binaryLimit, false)"
                : value + ", false";
    }

    private static void RequireAliases(NpgsqlDataReader reader, UniqueConstraint stableKey)
    {
        var aliases = SelectionKeyAliases.ForKey(stableKey);
        if (reader.FieldCount != aliases.Count || aliases.Where((alias, index) => !string.Equals(reader.GetName(index), alias, StringComparison.Ordinal)).Any())
            throw new RawSqlValidationException("Raw SQL must project exactly the declared stable-key aliases.");
    }
}
