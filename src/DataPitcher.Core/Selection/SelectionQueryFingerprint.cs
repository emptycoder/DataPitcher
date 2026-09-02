using System.Security.Cryptography;
using System.Text;
namespace DataPitcher.Core.Selection;
public static class SelectionQueryFingerprint
{
    public static string Sha256(SelectionQuery query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText(SelectionQueryNormalizer.Normalize(query)))));
    public static string CanonicalText(SelectionQuery query) => "root(" + Table(query.Root.Table) + ":" + query.Root.Alias + ":" + string.Join(",", query.RootStableKey.Constraint!.Columns) + ")|joins(" + string.Join(",", query.Joins.Select(JoinText)) + ")|where(" + (PredicateText(query.Predicate) ?? "") + ")";
    internal static string? PredicateText(SelectionPredicate? predicate) => predicate switch
    {
        null => null, AndPredicate(var terms) => "and(" + string.Join(",", terms.Select(PredicateText)) + ")", OrPredicate(var terms) => "or(" + string.Join(",", terms.Select(PredicateText)) + ")", NotPredicate(var term) => "not(" + PredicateText(term) + ")",
        ComparisonPredicate(var c, var o, var v) => Name(o) + "(" + Column(c) + ":" + Value(v) + ")", BetweenPredicate(var c, var l, var u) => "between(" + Column(c) + ":" + Value(l) + ":" + Value(u) + ")", SetPredicate(var c, var n, var values) => (n ? "notin" : "in") + "(" + Column(c) + ":" + string.Join(",", values.Select(Value)) + ")",
        NullPredicate(var c, var n) => (n ? "notnull" : "null") + "(" + Column(c) + ")", TextPredicate(var c, var m, var v) => m.ToString().ToLowerInvariant() + "(" + Column(c) + ":" + Value(v) + ")", BooleanPredicate(var c, var v) => "bool(" + Column(c) + ":" + Value(v) + ")", TemporalRangePredicate(var c, var k, var l, var u) => k.ToString().ToLowerInvariant() + "(" + Column(c) + ":" + Value(l) + ":" + Value(u) + ")",
        ExistsPredicate(var table, var alias, var links, var inner, var n) => (n ? "notexists" : "exists") + "(" + Table(table) + ":" + alias + ":" + string.Join(",", links.Select(x => Column(x.OuterColumn) + "=" + x.InnerColumn)) + ":" + PredicateText(inner) + ")", _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };
    private static string JoinText(SelectionJoin join) => join is ForeignKeyJoin f ? "fk(" + f.FromAlias + ":" + f.Alias + ":" + Table(f.ForeignKey.ChildTable) + ":" + string.Join(",", f.ForeignKey.ChildColumns) + "=" + Table(f.ForeignKey.ParentTable) + ":" + string.Join(",", f.ForeignKey.ParentColumns) + ":" + f.Direction + ")" : "manual(" + join.FromAlias + ":" + join.Alias + ":" + Table(((ManualJoin)join).Table) + ":" + string.Join(",", ((ManualJoin)join).Pairs.Select(x => x.FromColumn + "=" + x.ToColumn)) + ")";
    private static string Name(SelectionComparison value) => value switch { SelectionComparison.Equal => "eq", SelectionComparison.NotEqual => "ne", SelectionComparison.GreaterThan => "gt", SelectionComparison.GreaterOrEqual => "ge", SelectionComparison.LessThan => "lt", SelectionComparison.LessOrEqual => "le", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static string Value(SelectionParameterValue value) => value.ClrType.FullName!.ToLowerInvariant() + ":" + System.Text.Json.JsonSerializer.Serialize(value.Value, value.ClrType);
    private static string Column(SelectionColumn column) => column.Alias + "." + column.Name; private static string Table(DataPitcher.Core.Schema.TableDefinition table) => table.Schema + "." + table.Name;
}
