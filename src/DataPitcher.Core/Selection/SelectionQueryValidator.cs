using System.Text.RegularExpressions;
using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Selection;
public static partial class SelectionQueryValidator
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z")] private static partial Regex AliasPattern();
    public static void Validate(SelectionQuery query)
    {
        if (!query.Schema.Tables.Contains(query.Root.Table)) throw new ArgumentException("Root table is not in selection schema.");
        if (query.RootStableKey.Constraint is null || !query.Root.Table.Columns.Select(x => x.Name).Intersect(query.RootStableKey.Constraint.Columns).SequenceEqual(query.RootStableKey.Constraint.Columns)) throw new ArgumentException("Root must declare an existing stable key.");
        var aliases = new Dictionary<string, TableDefinition>(StringComparer.Ordinal); AddAlias(aliases, query.Root.Alias, query.Root.Table);
        foreach (var join in query.Joins) ValidateJoin(query.Schema, aliases, join);
        if (query.Predicate is not null) ValidatePredicate(query.Schema, query.Predicate, aliases);
    }
    private static void ValidateJoin(SelectionSchema schema, Dictionary<string, TableDefinition> aliases, SelectionJoin join)
    {
        if (!aliases.TryGetValue(join.FromAlias, out var from)) throw new ArgumentException("Join source alias is not in scope.");
        if (join is ForeignKeyJoin known) { if (!schema.ForeignKeys.Contains(known.ForeignKey)) throw new ArgumentException("Foreign-key join is not in selection schema."); var expected = known.Direction == RelationshipDirection.Forward ? known.ForeignKey.ChildTable : known.ForeignKey.ParentTable; if (from != expected) throw new ArgumentException("Foreign-key path does not start at its source alias table."); AddAlias(aliases, known.Alias, known.Direction == RelationshipDirection.Forward ? known.ForeignKey.ParentTable : known.ForeignKey.ChildTable); return; }
        var manual = (ManualJoin)join; if (!schema.Tables.Contains(manual.Table)) throw new ArgumentException("Manual join table is not in selection schema."); if (manual.Pairs.Count == 0) throw new ArgumentException("Manual join must contain a column pair."); foreach (var pair in manual.Pairs) if (Column(from, pair.FromColumn).ClrType != Column(manual.Table, pair.ToColumn).ClrType) throw new ArgumentException("Manual join columns must have identical CLR types."); AddAlias(aliases, manual.Alias, manual.Table);
    }
    private static void ValidatePredicate(SelectionSchema schema, SelectionPredicate predicate, IReadOnlyDictionary<string, TableDefinition> aliases)
    {
        switch (predicate)
        {
            case AndPredicate conjunction when conjunction.Terms.Count >= 2: Each(schema, conjunction.Terms, aliases); return;
            case OrPredicate disjunction when disjunction.Terms.Count >= 2: Each(schema, disjunction.Terms, aliases); return;
            case NotPredicate not: ValidatePredicate(schema, not.Term, aliases); return;
            case ComparisonPredicate comparison when Enum.IsDefined(typeof(SelectionComparison), comparison.Operator): Value(Column(aliases, comparison.Column), comparison.Value); return;
            case BetweenPredicate between: Values(Column(aliases, between.Column), between.Lower, between.Upper); return;
            case SetPredicate set when set.Values.Count > 0: EachValue(Column(aliases, set.Column), set.Values); return;
            case NullPredicate nullTest: Column(aliases, nullTest.Column); return;
            case TextPredicate text when Enum.IsDefined(typeof(TextMatch), text.Match) && Column(aliases, text.Column).ClrType == typeof(string): Value(Column(aliases, text.Column), text.Value); return;
            case BooleanPredicate boolean when Column(aliases, boolean.Column).ClrType == typeof(bool): Value(Column(aliases, boolean.Column), boolean.Value); return;
            case TemporalRangePredicate range when IsTemporal(range, aliases): Values(Column(aliases, range.Column), range.Lower, range.Upper); return;
            case ExistsPredicate exists: Exists(schema, exists, aliases); return;
            default: throw new ArgumentException("Predicate is not semantically valid for its column type.");
        }
    }
    private static void Exists(SelectionSchema schema, ExistsPredicate exists, IReadOnlyDictionary<string, TableDefinition> outer) { if (!schema.Tables.Contains(exists.Table)) throw new ArgumentException("EXISTS table is not in selection schema."); if (exists.Correlations.Count == 0) throw new ArgumentException("EXISTS requires a correlation."); var scope = new Dictionary<string, TableDefinition>(outer, StringComparer.Ordinal); AddAlias(scope, exists.Alias, exists.Table); foreach (var c in exists.Correlations) { var outerColumn = Column(outer, c.OuterColumn); if (outerColumn.ClrType != Column(exists.Table, c.InnerColumn).ClrType) throw new ArgumentException("EXISTS correlation columns must have identical CLR types."); } if (exists.Predicate is not null) ValidatePredicate(schema, exists.Predicate, scope); }
    private static bool IsTemporal(TemporalRangePredicate range, IReadOnlyDictionary<string, TableDefinition> aliases) => (range.Kind, Column(aliases, range.Column).ClrType) switch { (TemporalKind.Date, var type) when type == typeof(DateOnly) => true, (TemporalKind.Time, var type) when type == typeof(TimeOnly) => true, (TemporalKind.DateTime, var type) when type == typeof(DateTime) => true, _ => false };
    private static void AddAlias(IDictionary<string, TableDefinition> aliases, string alias, TableDefinition table) { if (!AliasPattern().IsMatch(alias)) throw new ArgumentException("Alias must match [A-Za-z_][A-Za-z0-9_]*."); if (!aliases.TryAdd(alias, table)) throw new ArgumentException("Alias is already in use."); }
    private static ColumnDefinition Column(IReadOnlyDictionary<string, TableDefinition> aliases, SelectionColumn column) => aliases.TryGetValue(column.Alias, out var table) ? Column(table, column.Name) : throw new ArgumentException("Column alias is not in scope.");
    private static ColumnDefinition Column(TableDefinition table, string name) => table.Columns.SingleOrDefault(x => StringComparer.Ordinal.Equals(x.Name, name)) ?? throw new ArgumentException("Column does not exist.");
    private static void Value(ColumnDefinition column, SelectionParameterValue value) { if (column.ClrType != value.ClrType) throw new ArgumentException("Selection parameter CLR type must match the column CLR type."); }
    private static void Values(ColumnDefinition column, params SelectionParameterValue[] values) { foreach (var value in values) Value(column, value); }
    private static void Each(SelectionSchema schema, IEnumerable<SelectionPredicate> values, IReadOnlyDictionary<string, TableDefinition> aliases) { foreach (var value in values) ValidatePredicate(schema, value, aliases); }
    private static void EachValue(ColumnDefinition column, IEnumerable<SelectionParameterValue> values) { foreach (var value in values) Value(column, value); }
}
