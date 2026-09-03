using System.Globalization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerSelectionExecutor(string sourceConnectionString, SqlServerSchemaSnapshot schema)
    : ISelectionExecutor
{
    public async Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken)
    {
        if (selection.IsRawSql)
            RawSqlSafetyValidator.Validate(RawSqlDialect.SqlServer, selection.CommandText);
        await using var connection = await OpenAsync(cancellationToken);
        var source = Source(selection);
        await using var command = Command(
            connection,
            "/* DataPitcher.Selection.Validate */ " + source.Prefix + " SELECT TOP (1) * FROM " + source.From,
            selection
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        RequireAliases(reader, selection.RootStableKey);
    }

    public async Task<SelectionKeySet> ReadKeysAsync(
        GeneratedSelectionSql selection,
        int maximumResultSize,
        CancellationToken cancellationToken
    )
    {
        var aliases = Aliases(selection.RootStableKey);
        await using var connection = await OpenAsync(cancellationToken);
        var source = Source(selection);
        await using var command = Command(
            connection,
            "/* DataPitcher.Selection.Keys */ "
                + source.Prefix
                + " SELECT DISTINCT TOP (@take) "
                + aliases
                + " FROM "
                + source.From
                + " ORDER BY "
                + aliases,
            selection
        );
        command.Parameters.AddWithValue("@take", checked(maximumResultSize + 1));
        var keys = await ReadKeysAsync(command, selection.RootStableKey, cancellationToken);
        if (keys.Count > maximumResultSize)
            throw new SelectionResultLimitExceededException(maximumResultSize);
        return new SelectionKeySet(selection.RootTable, keys);
    }

    public async Task<SelectionPreview> PreviewAsync(
        GeneratedSelectionSql selection,
        int rowLimit,
        int textLimit,
        int binaryLimit,
        CancellationToken cancellationToken
    )
    {
        var root = schema.Table(selection.RootTable.Name).Definition;
        var aliases = Aliases(selection.RootStableKey);
        var rootAlias = SqlServerIdentifier.Quote("root");
        var keys = SelectionKeyAliases.ForKey(selection.RootStableKey);
        var projection = string.Join(
            ", ",
            keys.Select(SqlServerIdentifier.Quote)
                .Concat(root.Columns.Select(column => PreviewProjection(rootAlias, column)))
        );
        var join = string.Join(
            " AND ",
            selection.RootStableKey.Columns.Select(
                (column, index) =>
                    rootAlias
                    + "."
                    + SqlServerIdentifier.Quote(column)
                    + " = keys."
                    + SqlServerIdentifier.Quote(keys[index])
            )
        );
        var order = string.Join(
            ", ",
            selection.RootStableKey.Columns.Select(column => rootAlias + "." + SqlServerIdentifier.Quote(column))
        );
        var source = Source(selection);
        var cte = source.IsCte ? source.Prefix : "WITH selection AS (" + CommandText(selection) + "\n)";
        var sql =
            "/* DataPitcher.Selection.Preview */ "
            + cte
            + ", keys AS (SELECT DISTINCT TOP (@previewLimit) "
            + aliases
            + " FROM "
            + (source.IsCte ? source.From : "selection")
            + " ORDER BY "
            + aliases
            + ") SELECT "
            + projection
            + " FROM "
            + SqlServerIdentifier.Qualified(root.Schema, root.Name)
            + " AS "
            + rootAlias
            + " INNER JOIN keys ON "
            + join
            + " ORDER BY "
            + order;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, sql, selection);
        command.Parameters.AddWithValue("@previewLimit", rowLimit);
        command.Parameters.AddWithValue("@textLimit", textLimit);
        command.Parameters.AddWithValue("@binaryLimit", binaryLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadPreviewAsync(reader, root, selection.RootStableKey, cancellationToken);
    }

    public async Task<long> CountAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken)
    {
        var aliases = Aliases(selection.RootStableKey);
        await using var connection = await OpenAsync(cancellationToken);
        var source = Source(selection);
        await using var command = Command(
            connection,
            "/* DataPitcher.Selection.Count */ "
                + source.Prefix
                + " SELECT COUNT_BIG(*) FROM (SELECT DISTINCT "
                + aliases
                + " FROM "
                + source.From
                + ") AS keys",
            selection
        );
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static SqlCommand Command(SqlConnection connection, string sql, GeneratedSelectionSql selection)
    {
        var command = new SqlCommand(sql, connection);
        foreach (var parameter in selection.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<List<StableKey>> ReadKeysAsync(
        SqlCommand command,
        UniqueConstraint stableKey,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<StableKey>();
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(
                new StableKey(
                    stableKey.Columns.Select((column, index) => new KeyComponent(column, reader.GetValue(index)))
                )
            );
        return keys;
    }

    private async Task<SelectionPreview> ReadPreviewAsync(
        SqlDataReader reader,
        TableDefinition root,
        UniqueConstraint stableKey,
        CancellationToken cancellationToken
    )
    {
        var columns = root
            .Columns.Select(column => new SelectionPreviewColumn(
                column.Name,
                stableKey.Columns.Contains(column.Name, StringComparer.Ordinal),
                schema.ForeignKeys.Any(foreignKey =>
                    string.Equals(foreignKey.ChildTable.Schema, root.Schema, StringComparison.Ordinal)
                    && string.Equals(foreignKey.ChildTable.Name, root.Name, StringComparison.Ordinal)
                    && foreignKey.ChildColumns.Contains(column.Name, StringComparer.Ordinal)
                ),
                column.IsGenerated
            ))
            .ToArray();
        var rows = new List<SelectionPreviewRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new StableKey(
                stableKey.Columns.Select((column, index) => new KeyComponent(column, reader.GetValue(index)))
            );
            var values = new Dictionary<string, SelectionPreviewCell>(StringComparer.Ordinal);
            for (var index = 0; index < root.Columns.Count; index++)
            {
                var offset = stableKey.Columns.Count + (index * 2);
                values.Add(
                    root.Columns[index].Name,
                    new SelectionPreviewCell(
                        reader.IsDBNull(offset) ? null : reader.GetValue(offset),
                        reader.GetBoolean(offset + 1)
                    )
                );
            }
            rows.Add(new SelectionPreviewRow(key, values));
        }
        return new SelectionPreview(columns, rows);
    }

    private static string Aliases(UniqueConstraint stableKey) =>
        string.Join(", ", SelectionKeyAliases.ForKey(stableKey).Select(SqlServerIdentifier.Quote));

    private static string CommandText(GeneratedSelectionSql selection) =>
        selection.IsRawSql ? RawSqlSafetyValidator.RemoveTrailingOrderBy(selection.CommandText) : selection.CommandText;

    private static (string Prefix, string From, bool IsCte) Source(GeneratedSelectionSql selection)
    {
        var text = CommandText(selection);
        return selection.IsRawSql && RawSqlSafetyValidator.TrySplitLeadingCte(text, out var ctes, out var query)
            ? (";" + ctes + ", selection AS (" + query + "\n)", "selection", true)
            : (string.Empty, "(" + text + "\n) AS selection", false);
    }

    private static string PreviewProjection(string rootAlias, ColumnDefinition column)
    {
        var value = rootAlias + "." + SqlServerIdentifier.Quote(column.Name);
        return column.ClrType == typeof(string)
                ? "CASE WHEN LEN("
                    + value
                    + ") > @textLimit THEN LEFT("
                    + value
                    + ", @textLimit) ELSE "
                    + value
                    + " END, CAST(CASE WHEN LEN("
                    + value
                    + ") > @textLimit THEN 1 ELSE 0 END AS bit)"
            : column.ClrType == typeof(byte[])
                ? "CASE WHEN DATALENGTH("
                    + value
                    + ") > @binaryLimit THEN SUBSTRING("
                    + value
                    + ", 1, @binaryLimit) ELSE "
                    + value
                    + " END, CAST(CASE WHEN DATALENGTH("
                    + value
                    + ") > @binaryLimit THEN 1 ELSE 0 END AS bit)"
            : value + ", CAST(0 AS bit)";
    }

    private static void RequireAliases(SqlDataReader reader, UniqueConstraint stableKey)
    {
        var aliases = SelectionKeyAliases.ForKey(stableKey);
        if (
            reader.FieldCount != aliases.Count
            || aliases
                .Where((alias, index) => !string.Equals(reader.GetName(index), alias, StringComparison.Ordinal))
                .Any()
        )
            throw new RawSqlValidationException("Raw SQL must project exactly the declared stable-key aliases.");
    }
}
