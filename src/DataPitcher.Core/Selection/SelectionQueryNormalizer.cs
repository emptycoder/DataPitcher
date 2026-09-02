namespace DataPitcher.Core.Selection;
public static class SelectionQueryNormalizer
{
    public static SelectionQuery Normalize(SelectionQuery query) => new(query.Schema, query.Root, query.RootStableKey, query.Joins, Normalize(query.Predicate));
    public static SelectionPredicate? Normalize(SelectionPredicate? predicate) => predicate switch
    {
        null => null, NotPredicate(var term) when Normalize(term) is NotPredicate(var inner) => Normalize(inner), NotPredicate(var term) => new NotPredicate(Normalize(term)!),
        AndPredicate(var terms) => Boolean(true, terms), OrPredicate(var terms) => Boolean(false, terms), SetPredicate(var column, var negated, var values) => new SetPredicate(column, negated, values.Distinct().OrderBy(ValueKey, StringComparer.Ordinal).ToArray()), ExistsPredicate(var table, var alias, var correlations, var inner, var negated) => new ExistsPredicate(table, alias, correlations, Normalize(inner), negated),
        _ => predicate
    };
    private static SelectionPredicate Boolean(bool and, IReadOnlyList<SelectionPredicate> terms)
    {
        var flattened = terms.Select(Normalize).SelectMany(x => and && x is AndPredicate a ? a.Terms : !and && x is OrPredicate o ? o.Terms : [x!]).Distinct().OrderBy(SelectionQueryFingerprint.PredicateText, StringComparer.Ordinal).ToArray();
        return and ? new AndPredicate(flattened) : new OrPredicate(flattened);
    }
    internal static string ValueKey(SelectionParameterValue value) => value.ClrType.FullName + ":" + System.Text.Json.JsonSerializer.Serialize(value.Value, value.ClrType);
}
