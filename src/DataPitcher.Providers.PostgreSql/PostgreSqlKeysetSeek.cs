using DataPitcher.Core.Identity;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed record PostgreSqlSeekQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

public static class PostgreSqlKeysetSeek
{
    public static PostgreSqlSeekQuery Build(PostgreSqlWriteTable table, StableKey after, int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var columns = table.StableKeyColumns;
        var predicates = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        for (var index = 0; index < columns.Count; index++)
        {
            var equal = string.Join(
                " AND ",
                Enumerable.Range(0, index).Select(i => Expression(columns[i]) + "=@k" + i)
            );
            predicates.Add((equal.Length == 0 ? "" : equal + " AND ") + Expression(columns[index]) + ">@k" + index);
        }
        for (var index = 0; index < columns.Count; index++)
            parameters.Add(
                new NpgsqlParameter("k" + index, columns[index].ProviderType)
                {
                    Value = after.Components.Single(x => x.Column == columns[index].Name).Value!,
                }
            );
        parameters.Add(new NpgsqlParameter("limit", limit));
        var order = string.Join(", ", columns.Select(Expression));
        var select = string.Join(
            ", ",
            table.InsertColumns.Select(column => "s." + PostgreSqlIdentifier.Quote(column.Name))
        );
        return new PostgreSqlSeekQuery(
            "SELECT "
                + select
                + " FROM "
                + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                + " s WHERE ("
                + string.Join(" OR ", predicates)
                + ") ORDER BY "
                + order
                + " LIMIT @limit",
            Array.AsReadOnly(parameters.ToArray())
        );
    }

    private static string Expression(PostgreSqlWriteColumn column) =>
        "s."
        + PostgreSqlIdentifier.Quote(column.Name)
        + (column.ProviderType == NpgsqlDbType.Text ? " COLLATE \"C\"" : "");
}
