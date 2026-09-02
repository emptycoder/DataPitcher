using System.Text;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlSelectionSqlGenerator : ISelectionSqlCompiler
{
    private static readonly string[] ComparisonTokens = [" = ", " <> ", " > ", " >= ", " < ", " <= "];
    private static readonly string[] LikePrefixes = [" LIKE ('%' || ", " LIKE (", " LIKE ('%' || "];
    private static readonly string[] LikeSuffixes = [" || '%') ESCAPE '\\'", " || '%') ESCAPE '\\'", ") ESCAPE '\\'"];
    public GeneratedSelectionSql Compile(SelectionQuery source)
    {
        var query = SelectionQueryNormalizer.Normalize(source); var writer = new Writer(); writer.Token("SELECT DISTINCT "); Columns(writer, query.Root.Alias, query.RootStableKey.Constraint!.Columns); writer.Token(" FROM "); Table(writer, query.Root.Table); writer.Token(" AS "); writer.Identifier(query.Root.Alias);
        foreach (var join in query.Joins) Join(writer, join); if (query.Predicate is not null) { writer.Token(" WHERE "); Predicate(writer, query.Predicate); }
        return new GeneratedSelectionSql(writer.Text, query.Root.Table, query.RootStableKey.Constraint!, writer.Parameters);
    }
    private static void Join(Writer w, SelectionJoin join) { var (table, left, right) = join is ForeignKeyJoin f && f.Direction == RelationshipDirection.Forward ? (f.ForeignKey.ParentTable, f.ForeignKey.ChildColumns, f.ForeignKey.ParentColumns) : join is ForeignKeyJoin reverse ? (reverse.ForeignKey.ChildTable, reverse.ForeignKey.ParentColumns, reverse.ForeignKey.ChildColumns) : (((ManualJoin)join).Table, ((ManualJoin)join).Pairs.Select(x => x.FromColumn).ToArray(), ((ManualJoin)join).Pairs.Select(x => x.ToColumn).ToArray()); w.Token(" INNER JOIN "); Table(w, table); w.Token(" AS "); w.Identifier(join.Alias); w.Token(" ON "); Pairs(w, join.FromAlias, left, join.Alias, right); }
    private static void Predicate(Writer w, SelectionPredicate p)
    {
        switch (p)
        {
            case AndPredicate a: Group(w, " AND ", a.Terms); break;
            case OrPredicate o: Group(w, " OR ", o.Terms); break;
            case NotPredicate n: w.Token("NOT ("); Predicate(w, n.Term); w.Token(")"); break;
            case ComparisonPredicate c: Column(w, c.Column); w.Token(ComparisonTokens[(int)c.Operator]); w.Parameter(c.Value); break;
            case BetweenPredicate b: Column(w, b.Column); w.Token(" BETWEEN "); w.Parameter(b.Lower); w.Token(" AND "); w.Parameter(b.Upper); break;
            case SetPredicate s: Column(w, s.Column); w.Token(s.Negated ? " NOT IN (" : " IN ("); for (var i = 0; i < s.Values.Count; i++) { if (i > 0) w.Token(", "); w.Parameter(s.Values[i]); } w.Token(")"); break;
            case NullPredicate n: Column(w, n.Column); w.Token(n.Negated ? " IS NOT NULL" : " IS NULL"); break;
            case TextPredicate t: Column(w, t.Column); w.Token(LikePrefixes[(int)t.Match]); w.Parameter(new(typeof(string), EscapeLike((string)t.Value.Value))); w.Token(LikeSuffixes[(int)t.Match]); break;
            case BooleanPredicate b: Column(w, b.Column); w.Token(" = "); w.Parameter(b.Value); break;
            case TemporalRangePredicate r: Column(w, r.Column); w.Token(" BETWEEN "); w.Parameter(r.Lower); w.Token(" AND "); w.Parameter(r.Upper); break;
            case ExistsPredicate e: w.Token(e.Negated ? "NOT EXISTS (SELECT 1 FROM " : "EXISTS (SELECT 1 FROM "); Table(w, e.Table); w.Token(" AS "); w.Identifier(e.Alias); w.Token(" WHERE "); for (var i = 0; i < e.Correlations.Count; i++) { if (i > 0) w.Token(" AND "); Column(w, e.Correlations[i].OuterColumn); w.Token(" = "); w.Identifier(e.Alias); w.Token("."); w.Identifier(e.Correlations[i].InnerColumn); } if (e.Predicate is not null) { w.Token(" AND "); Predicate(w, e.Predicate); } w.Token(")"); break;
        }
    }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static void Group(Writer w, string token, IReadOnlyList<SelectionPredicate> terms) { w.Token("("); for (var i = 0; i < terms.Count; i++) { if (i > 0) w.Token(token); Predicate(w, terms[i]); } w.Token(")"); }
    private static void Columns(Writer w, string alias, IReadOnlyList<string> columns) { for (var i = 0; i < columns.Count; i++) { if (i > 0) w.Token(", "); w.Identifier(alias); w.Token("."); w.Identifier(columns[i]); w.Token(" AS "); w.Identifier(SelectionKeyAliases.For(i)); } }
    private static void Pairs(Writer w, string leftAlias, IReadOnlyList<string> left, string rightAlias, IReadOnlyList<string> right) { for (var i = 0; i < left.Count; i++) { if (i > 0) w.Token(" AND "); w.Identifier(leftAlias); w.Token("."); w.Identifier(left[i]); w.Token(" = "); w.Identifier(rightAlias); w.Token("."); w.Identifier(right[i]); } }
    private static void Column(Writer w, SelectionColumn c) { w.Identifier(c.Alias); w.Token("."); w.Identifier(c.Name); }
    private static void Table(Writer w, TableDefinition t) { w.Identifier(t.Schema); w.Token("."); w.Identifier(t.Name); }
    private sealed class Writer { private readonly StringBuilder _text = new(); private readonly List<SelectionSqlParameter> _parameters = []; public string Text => _text.ToString(); public IReadOnlyList<SelectionSqlParameter> Parameters => _parameters; public void Token(string token) => _text.Append(token); public void Identifier(string identifier) => _text.Append(PostgreSqlIdentifier.Quote(identifier)); public void Parameter(SelectionParameterValue value) { var name = "@p" + _parameters.Count; _parameters.Add(new(name, value.ClrType, value.Value)); _text.Append(name); } }
}
