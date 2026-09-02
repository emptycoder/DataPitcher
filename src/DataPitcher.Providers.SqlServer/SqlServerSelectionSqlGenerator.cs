using System.Text;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerSelectionSqlGenerator : ISelectionSqlCompiler
{
    private static readonly string[] ComparisonTokens = [" = ", " <> ", " > ", " >= ", " < ", " <= "];
    private static readonly string[] LikePrefixes = [" LIKE ('%' + ", " LIKE (", " LIKE ('%' + "];
    private static readonly string[] LikeSuffixes = [" + '%') ESCAPE '\\'", " + '%') ESCAPE '\\'", ") ESCAPE '\\'"];

    public GeneratedSelectionSql Compile(SelectionQuery source)
    {
        var query = SelectionQueryNormalizer.Normalize(source);
        var writer = new Writer();
        writer.Token("SELECT DISTINCT ");
        Columns(writer, query.Root.Alias, query.RootStableKey.Constraint!.Columns);
        writer.Token(" FROM ");
        Table(writer, query.Root.Table);
        writer.Token(" AS ");
        writer.Identifier(query.Root.Alias);
        foreach (var join in query.Joins) Join(writer, join);
        if (query.Predicate is not null) { writer.Token(" WHERE "); Predicate(writer, query.Predicate); }
        return new GeneratedSelectionSql(writer.Text, query.Root.Table, query.RootStableKey.Constraint!, writer.Parameters);
    }

    private static void Join(Writer writer, SelectionJoin join)
    {
        var (table, left, right) = join is ForeignKeyJoin forward && forward.Direction == RelationshipDirection.Forward
            ? (forward.ForeignKey.ParentTable, forward.ForeignKey.ChildColumns, forward.ForeignKey.ParentColumns)
            : join is ForeignKeyJoin reverse
                ? (reverse.ForeignKey.ChildTable, reverse.ForeignKey.ParentColumns, reverse.ForeignKey.ChildColumns)
                : (((ManualJoin)join).Table, ((ManualJoin)join).Pairs.Select(pair => pair.FromColumn).ToArray(), ((ManualJoin)join).Pairs.Select(pair => pair.ToColumn).ToArray());
        writer.Token(" INNER JOIN ");
        Table(writer, table);
        writer.Token(" AS ");
        writer.Identifier(join.Alias);
        writer.Token(" ON ");
        Pairs(writer, join.FromAlias, left, join.Alias, right);
    }

    private static void Predicate(Writer writer, SelectionPredicate predicate)
    {
        switch (predicate)
        {
            case AndPredicate andPredicate: Group(writer, " AND ", andPredicate.Terms); break;
            case OrPredicate orPredicate: Group(writer, " OR ", orPredicate.Terms); break;
            case NotPredicate notPredicate: writer.Token("NOT ("); Predicate(writer, notPredicate.Term); writer.Token(")"); break;
            case ComparisonPredicate comparison: Column(writer, comparison.Column); writer.Token(ComparisonTokens[(int)comparison.Operator]); writer.Parameter(comparison.Value); break;
            case BetweenPredicate between: Column(writer, between.Column); writer.Token(" BETWEEN "); writer.Parameter(between.Lower); writer.Token(" AND "); writer.Parameter(between.Upper); break;
            case SetPredicate set: Column(writer, set.Column); writer.Token(set.Negated ? " NOT IN (" : " IN ("); for (var index = 0; index < set.Values.Count; index++) { if (index > 0) writer.Token(", "); writer.Parameter(set.Values[index]); } writer.Token(")"); break;
            case NullPredicate nullPredicate: Column(writer, nullPredicate.Column); writer.Token(nullPredicate.Negated ? " IS NOT NULL" : " IS NULL"); break;
            case TextPredicate text: Column(writer, text.Column); writer.Token(LikePrefixes[(int)text.Match]); writer.Parameter(new(typeof(string), EscapeLike((string)text.Value.Value))); writer.Token(LikeSuffixes[(int)text.Match]); break;
            case BooleanPredicate boolean: Column(writer, boolean.Column); writer.Token(" = "); writer.Parameter(boolean.Value); break;
            case TemporalRangePredicate range: Column(writer, range.Column); writer.Token(" BETWEEN "); writer.Parameter(range.Lower); writer.Token(" AND "); writer.Parameter(range.Upper); break;
            case ExistsPredicate exists: writer.Token(exists.Negated ? "NOT EXISTS (SELECT 1 FROM " : "EXISTS (SELECT 1 FROM "); Table(writer, exists.Table); writer.Token(" AS "); writer.Identifier(exists.Alias); writer.Token(" WHERE "); for (var index = 0; index < exists.Correlations.Count; index++) { if (index > 0) writer.Token(" AND "); Column(writer, exists.Correlations[index].OuterColumn); writer.Token(" = "); writer.Identifier(exists.Alias); writer.Token("."); writer.Identifier(exists.Correlations[index].InnerColumn); } if (exists.Predicate is not null) { writer.Token(" AND "); Predicate(writer, exists.Predicate); } writer.Token(")"); break;
        }
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static void Group(Writer writer, string token, IReadOnlyList<SelectionPredicate> terms) { writer.Token("("); for (var index = 0; index < terms.Count; index++) { if (index > 0) writer.Token(token); Predicate(writer, terms[index]); } writer.Token(")"); }
    private static void Columns(Writer writer, string alias, IReadOnlyList<string> columns) { for (var index = 0; index < columns.Count; index++) { if (index > 0) writer.Token(", "); writer.Identifier(alias); writer.Token("."); writer.Identifier(columns[index]); writer.Token(" AS "); writer.Identifier(SelectionKeyAliases.For(index)); } }
    private static void Pairs(Writer writer, string leftAlias, IReadOnlyList<string> left, string rightAlias, IReadOnlyList<string> right) { for (var index = 0; index < left.Count; index++) { if (index > 0) writer.Token(" AND "); writer.Identifier(leftAlias); writer.Token("."); writer.Identifier(left[index]); writer.Token(" = "); writer.Identifier(rightAlias); writer.Token("."); writer.Identifier(right[index]); } }
    private static void Column(Writer writer, SelectionColumn column) { writer.Identifier(column.Alias); writer.Token("."); writer.Identifier(column.Name); }
    private static void Table(Writer writer, TableDefinition table) { writer.Identifier(table.Schema); writer.Token("."); writer.Identifier(table.Name); }

    private sealed class Writer
    {
        private readonly StringBuilder text = new();
        private readonly List<SelectionSqlParameter> parameters = [];
        public string Text => text.ToString();
        public IReadOnlyList<SelectionSqlParameter> Parameters => parameters;
        public void Token(string token) => text.Append(token);
        public void Identifier(string identifier) => text.Append(SqlServerIdentifier.Quote(identifier));
        public void Parameter(SelectionParameterValue value)
        {
            var name = "@p" + parameters.Count;
            parameters.Add(new SelectionSqlParameter(name, value.ClrType, value.Value));
            text.Append(name);
        }
    }
}
