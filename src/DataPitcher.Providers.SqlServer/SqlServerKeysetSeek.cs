using DataPitcher.Core.Identity;
using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

public sealed record SqlServerSeekQuery(string Sql, IReadOnlyList<SqlParameter> Parameters);

public static class SqlServerKeysetSeek
{
    public static SqlServerSeekQuery Build(SqlServerWriteTable table, StableKey? after, int limit, string join = "")
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var columns = table.StableKeyColumns;
        var predicates = new List<string>();
        var parameters = new List<SqlParameter>();
        for (var index = 0; index < columns.Count; index++)
        {
            var equal = string.Join(" AND ", Enumerable.Range(0, index).Select(prior => Expression(columns[prior]) + "=@k" + prior));
            predicates.Add((equal.Length == 0 ? "" : equal + " AND ") + Expression(columns[index]) + ">@k" + index);
        }
        if (after is not null)
            for (var index = 0; index < columns.Count; index++)
                parameters.Add(new SqlParameter("@k" + index, columns[index].ProviderType) { Value = after.Components.Single(component => component.Column == columns[index].Name).Value! });
        parameters.Add(new SqlParameter("@limit", System.Data.SqlDbType.Int) { Value = limit });
        var select = string.Join(",", table.InsertColumns.Select(column => "s." + SqlServerIdentifier.Quote(column.Name)));
        var where = after is null ? "" : " WHERE (" + string.Join(" OR ", predicates) + ")";
        return new SqlServerSeekQuery("SELECT TOP (@limit) " + select + " FROM " + SqlServerIdentifier.Qualified(table.Target.Schema, table.Target.Name) + " s" + join + where + " ORDER BY " + string.Join(",", columns.Select(Expression)), Array.AsReadOnly(parameters.ToArray()));
    }

    private static string Expression(SqlServerWriteColumn column) => "s." + SqlServerIdentifier.Quote(column.Name) + (column.ProviderType == System.Data.SqlDbType.NVarChar ? " COLLATE " + (column.Collation ?? throw new InvalidOperationException("Text stable keys require a catalog collation.")) : "");
}
