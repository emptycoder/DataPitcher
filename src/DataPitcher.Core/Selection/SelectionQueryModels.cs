using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Selection;
public enum SelectionComparison { Equal, NotEqual, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }
public enum TextMatch { Contains, StartsWith, EndsWith }
public enum TemporalKind { Date, Time, DateTime }
public enum RelationshipDirection { Forward, Reverse }
public sealed record SelectionParameterValue { public SelectionParameterValue(Type clrType, object value) { if (value is null || value.GetType() != clrType) throw new ArgumentException("Selection parameter value must have its declared non-null CLR type."); ClrType = clrType; Value = value; } public Type ClrType { get; } public object Value { get; } }
public sealed record SelectionSqlParameter(string Name, Type ClrType, object Value);
public sealed class GeneratedSelectionSql { public GeneratedSelectionSql(string commandText, TableDefinition rootTable, UniqueConstraint rootStableKey, IEnumerable<SelectionSqlParameter> parameters, bool isRawSql = false) { CommandText = commandText; RootTable = rootTable; RootStableKey = rootStableKey; Parameters = Array.AsReadOnly(parameters.ToArray()); IsRawSql = isRawSql; } public string CommandText { get; } public TableDefinition RootTable { get; } public UniqueConstraint RootStableKey { get; } public IReadOnlyList<SelectionSqlParameter> Parameters { get; } public bool IsRawSql { get; } }
public sealed record SelectionTableReference(TableDefinition Table, string Alias);
public sealed record SelectionColumn(string Alias, string Name);
public sealed record SelectionColumnPair(string FromColumn, string ToColumn);
public sealed record SelectionCorrelation(SelectionColumn OuterColumn, string InnerColumn);
public abstract record SelectionJoin(string FromAlias, string Alias);
public sealed record ForeignKeyJoin(string FromAlias, string Alias, ForeignKeyDefinition ForeignKey, RelationshipDirection Direction) : SelectionJoin(FromAlias, Alias);
public sealed record ManualJoin(string FromAlias, string Alias, TableDefinition Table, IReadOnlyList<SelectionColumnPair> Pairs) : SelectionJoin(FromAlias, Alias);
public abstract record SelectionPredicate;
public sealed record AndPredicate(IReadOnlyList<SelectionPredicate> Terms) : SelectionPredicate;
public sealed record OrPredicate(IReadOnlyList<SelectionPredicate> Terms) : SelectionPredicate;
public sealed record NotPredicate(SelectionPredicate Term) : SelectionPredicate;
public sealed record ComparisonPredicate(SelectionColumn Column, SelectionComparison Operator, SelectionParameterValue Value) : SelectionPredicate;
public sealed record BetweenPredicate(SelectionColumn Column, SelectionParameterValue Lower, SelectionParameterValue Upper) : SelectionPredicate;
public sealed record SetPredicate(SelectionColumn Column, bool Negated, IReadOnlyList<SelectionParameterValue> Values) : SelectionPredicate;
public sealed record NullPredicate(SelectionColumn Column, bool Negated) : SelectionPredicate;
public sealed record TextPredicate(SelectionColumn Column, TextMatch Match, SelectionParameterValue Value) : SelectionPredicate;
public sealed record BooleanPredicate(SelectionColumn Column, SelectionParameterValue Value) : SelectionPredicate;
public sealed record TemporalRangePredicate(SelectionColumn Column, TemporalKind Kind, SelectionParameterValue Lower, SelectionParameterValue Upper) : SelectionPredicate;
public sealed record ExistsPredicate(TableDefinition Table, string Alias, IReadOnlyList<SelectionCorrelation> Correlations, SelectionPredicate? Predicate, bool Negated) : SelectionPredicate;
public sealed class SelectionSchema { public SelectionSchema(IEnumerable<TableDefinition> tables, IEnumerable<ForeignKeyDefinition> foreignKeys) { Tables = Array.AsReadOnly(tables.Distinct().ToArray()); ForeignKeys = Array.AsReadOnly(foreignKeys.ToArray()); } public IReadOnlyList<TableDefinition> Tables { get; } public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; } }
public sealed class SelectionQuery { public SelectionQuery(SelectionSchema schema, SelectionTableReference root, StableKeySelection rootStableKey, IEnumerable<SelectionJoin> joins, SelectionPredicate? predicate) { Schema = schema; Root = root; RootStableKey = rootStableKey; Joins = Array.AsReadOnly(joins.ToArray()); Predicate = predicate; SelectionQueryValidator.Validate(this); } public SelectionSchema Schema { get; } public SelectionTableReference Root { get; } public StableKeySelection RootStableKey { get; } public IReadOnlyList<SelectionJoin> Joins { get; } public SelectionPredicate? Predicate { get; } }
