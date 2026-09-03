using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;
using NpgsqlTypes;

namespace DataPitcher.Providers.PostgreSql;

public sealed record PostgreSqlSeekQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

public static class PostgreSqlKeysetSeek
{
    /// <summary>
    /// Pages the source rows behind the sealed keys. When <paramref name="join"/> attaches the sealed key table
    /// (alias <c>f</c>), rows are ordered by closure generation descending before the key, so a table's ancestors
    /// are written before the rows that reference them and foreign keys stay enforced on the target.
    /// <paramref name="afterGeneration"/> is the generation of <paramref name="after"/> and is required then.
    /// </summary>
    public static PostgreSqlSeekQuery Build(
        PostgreSqlWriteTable table,
        StableKey? after,
        int limit,
        string join = "",
        int? afterGeneration = null
    )
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var byGeneration = join.Length > 0;
        if (byGeneration && after is not null && afterGeneration is null)
            throw new ArgumentException("The generation of the resume key is required.", nameof(afterGeneration));
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
        if (after is not null)
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
        var keyPredicate = "(" + string.Join(" OR ", predicates) + ")";
        string where;
        if (after is null)
            where = "";
        else if (byGeneration)
        {
            parameters.Add(new NpgsqlParameter("gen", afterGeneration!.Value));
            where = " WHERE (f.__generation < @gen OR (f.__generation = @gen AND " + keyPredicate + "))";
        }
        else
            where = " WHERE " + keyPredicate;
        return new PostgreSqlSeekQuery(
            "SELECT "
                + select
                + (byGeneration ? ", f.__generation" : "")
                + " FROM "
                + PostgreSqlIdentifier.Qualified(table.Target.Schema, table.Target.Name)
                + " s"
                + join
                + where
                + " ORDER BY "
                + (byGeneration ? "f.__generation DESC, " : "")
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
