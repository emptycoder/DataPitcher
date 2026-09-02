# DataPitcher Slice 5: Selection Query AST and SQL Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a provider-free typed selection-query AST and a pure PostgreSQL compiler that emits parameterized SQL selecting distinct stable keys for exactly one declared root table.

**Architecture:** `DataPitcher.Core` owns the schema-validated AST, predicate semantics, canonical normalization, and deterministic fingerprint; it references no provider, data-access, or ASP.NET package. `DataPitcher.Providers.PostgreSql` owns the SQL writer and invokes the existing `PostgreSqlIdentifier` for every identifier while returning Core parameter descriptions instead of executing commands. A generated query contains one `RootTable` and its declared stable-key constraint; joins and correlated `EXISTS` subqueries only constrain that root set, which preserves dependency-semantics section 2's rule that joined tables never become transfer roots.

**Tech Stack:** .NET SDK 10.0.400, C# latest, xUnit 2.9.3, Coverlet, SHA-256, `StringBuilder`, Npgsql 10.0.3 provider project, Bash.

---

## File Structure

- `src/DataPitcher.Core/Selection/SelectionQueryModels.cs` — typed root, join, predicate, parameter, compiled-SQL, and schema-scope value types.
- `src/DataPitcher.Core/Selection/SelectionQueryValidator.cs` — construction-time validation of schema membership, relationship paths, manual joins, aliases, operators, and parameter CLR types.
- `src/DataPitcher.Core/Selection/SelectionQueryNormalizer.cs` — canonical, defensive normalized form for equivalent Boolean trees and set values.
- `src/DataPitcher.Core/Selection/SelectionQueryFingerprint.cs` — SHA-256 fingerprint over the canonical structural representation.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs` — pure PostgreSQL `SELECT DISTINCT` compiler and closed SQL-token writer.
- `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj` — adds a test-only reference to the existing PostgreSQL project so compiler tests remain Docker-free.
- `tests/DataPitcher.UnitTests/Selection/SelectionQueryValidationTests.cs` — AST, operator, foreign-key/reverse-path, manual-join, alias, and typed-value validation tests.
- `tests/DataPitcher.UnitTests/Selection/SelectionQueryNormalizationTests.cs` — canonical-form, fingerprint, and bounded property-based normalization tests.
- `tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs` — SQL skeleton, parameterization, quoted-identifier, all-operator, and structural-SQL tests.
- `docs/plans/2026-09-02-slice-5-selection-query-ast.md` — this implementation plan.

## Scope and Deferrals

This slice contains only the typed model and provider SQL generation. It excludes execution, preview, counting, saved selections, plan sealing, and raw SQL mode because those require a live database. The AST and compiler are pure and fully testable now; proving generated SQL is separate from running it.

Raw SQL mode is deferred rather than represented by a string leaf. SQL is never assembled by ad hoc concatenation: the compiler accepts only quoted identifiers, fixed grammar tokens, and generated parameter names. Every operator value is a typed `SelectionParameterValue` that becomes a `SelectionSqlParameter`, never SQL text.

Core contains only CLR types, normalized structures, parameter descriptions, and hashing; its architecture test forbids Npgsql, ADO.NET, ASP.NET, and provider references. PostgreSQL syntax, `LIKE` escaping, and token serialization live in `DataPitcher.Providers.PostgreSql`. SQL Server gets its own compiler and quoter later; do not add a Core abstraction with one implementation.

Known `ForeignKeyDefinition` joins traverse child-to-parent or parent-to-child; consecutive joins form a path. Manual joins require schema tables, an in-scope source alias, a unique `[A-Za-z_][A-Za-z0-9_]*` alias, nonempty existing column pairs, and equal CLR types. This slice has only inner joins; outer joins, arbitrary fragments, grouping, ordering, extra projection, and set operations are deferred.

One `SelectionTableReference` has a non-null `StableKeySelection`. The compiler returns it in `GeneratedSelectionSql` and projects `DISTINCT` only its constraint-ordered columns. Thus joins constrain root keys but never add a transfer root, as required by dependency-semantics sections 2 and 3. Materializing `StableKey` values is later execution work.

`./scripts/test-unit.sh` runs unit and architecture checks without Docker; warnings are errors. Only `./scripts/test-all.sh` enforces merged 100 percent line, branch, and method coverage. Do not add a lane-local gate; Docker-capable CI runs the aggregate check.

### Task 1: Define and validate the typed selection AST

**Files:**
- Create: `src/DataPitcher.Core/Selection/SelectionQueryModels.cs`, `src/DataPitcher.Core/Selection/SelectionQueryValidator.cs`, `tests/DataPitcher.UnitTests/Selection/SelectionQueryValidationTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Selection/SelectionQueryValidationTests.cs`

- [ ] **Step 1: Write the failing AST and validation tests.**

```csharp
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using Xunit;

namespace DataPitcher.UnitTests.Selection;

public sealed class SelectionQueryValidationTests
{
    [Fact]
    public void SelectionQuery_AcceptsEveryTypedOperatorAndNestedBooleanTree()
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false), ("Name", typeof(string), true), ("Active", typeof(bool), false), ("Day", typeof(DateOnly), false), ("At", typeof(TimeOnly), false), ("Occurred", typeof(DateTime), false));
        var lines = T("sales", "Lines", ("Id", typeof(int), false), ("OrderId", typeof(int), false));
        var fk = new ForeignKeyDefinition("FK_Lines_Orders", lines, orders, ["OrderId"], ["Id"], true, true);
        var root = new SelectionTableReference(orders, "o");
        var p = SelectionQueryTestData.OperatorQueries().Select(query => query.Predicate!).ToArray();
        var query = new SelectionQuery(new([orders, lines], [fk]), root, Key(orders), [new ForeignKeyJoin("o", "l0", fk, RelationshipDirection.Reverse)], new NotPredicate(new OrPredicate([new AndPredicate(p), new NotPredicate(p[0])]))) ;
        Assert.Equal(orders, query.Root.Table); Assert.Equal(1, query.Joins.Count);
    }

    [Theory]
    [InlineData("9bad", "Alias must match")]
    [InlineData("o", "Alias is already in use")]
    public void SelectionQuery_RejectsInvalidManualJoinAlias(string alias, string message)
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false)); var other = T("sales", "Other", ("OrderId", typeof(int), false));
        var error = Assert.Throws<ArgumentException>(() => new SelectionQuery(new([orders, other], []), new(orders, "o"), Key(orders), [new ManualJoin("o", alias, other, [new("Id", "OrderId")])], null));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void SelectionQuery_RejectsUnknownManualColumnsIncompatibleTypesAndIncorrectParameterTypes()
    {
        var orders = T("sales", "Orders", ("Id", typeof(int), false)); var other = T("sales", "Other", ("Code", typeof(string), false)); var schema = new SelectionSchema([orders, other], []);
        Assert.Contains("does not exist", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [new ManualJoin("o", "x", other, [new("Missing", "Code")])], null)).Message);
        Assert.Contains("identical CLR types", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [new ManualJoin("o", "x", other, [new("Id", "Code")])], null)).Message);
        Assert.Contains("parameter CLR type", Assert.Throws<ArgumentException>(() => new SelectionQuery(schema, new(orders, "o"), Key(orders), [], new ComparisonPredicate(new("o", "Id"), SelectionComparison.Equal, V<string>("1")))).Message);
    }

    private static SelectionParameterValue V<T>(T value) where T : notnull => new(typeof(T), value);
    private static StableKeySelection Key(TableDefinition table) => new(new UniqueConstraint("PK_" + table.Name, ["Id"]));
    private static TableDefinition T(string schema, string name, params (string Name, Type Type, bool Nullable)[] columns) => new(schema, name, columns.Select(c => new ColumnDefinition(c.Name, c.Type, c.Nullable)).ToArray(), new UniqueConstraint("PK_" + name, ["Id"]), []);
}

internal static class SelectionQueryTestData
{
    private static readonly TableDefinition Orders = new("sales", "Orders", [new("Id", typeof(int), false), new("Name", typeof(string), true), new("Active", typeof(bool), false), new("Day", typeof(DateOnly), false), new("At", typeof(TimeOnly), false), new("Occurred", typeof(DateTime), false)], new("PK_Orders", ["Id"]), []);
    public static SelectionQuery Query(SelectionPredicate predicate) => new(new([Orders], []), new(Orders, "o"), new(Orders.PrimaryKey), [], predicate);
    public static ComparisonPredicate Id(int value) => new(new("o", "Id"), SelectionComparison.Equal, new(typeof(int), value));
    public static SelectionQuery QuotedRootAndJoin()
    {
        var orders = new TableDefinition("sales", "Order\"Rows", [new("Id", typeof(int), false), new("CustomerId", typeof(int), false)], new("PK_OrderRows", ["Id"]), []);
        var customers = new TableDefinition("sales", "Customers", [new("Id", typeof(int), false), new("RegionId", typeof(int), false)], new("PK_Customers", ["Id"]), []);
        var regions = new TableDefinition("sales", "Regions", [new("Id", typeof(int), false)], new("PK_Regions", ["Id"]), []);
        var foreignKey = new ForeignKeyDefinition("FK_Order_Customer", orders, customers, ["CustomerId"], ["Id"], true, true);
        return new(new([orders, customers, regions], [foreignKey]), new(orders, "r"), new(orders.PrimaryKey), [new ForeignKeyJoin("r", "c", foreignKey, RelationshipDirection.Forward), new ManualJoin("c", "g", regions, [new("RegionId", "Id")])], null);
    }
    public static IEnumerable<SelectionQuery> OperatorQueries()
    {
        SelectionParameterValue V<T>(T value) where T : notnull => new(typeof(T), value); var column = new SelectionColumn("o", "Id");
        foreach (var predicate in new SelectionPredicate[] { new ComparisonPredicate(column, SelectionComparison.Equal, V(1)), new ComparisonPredicate(column, SelectionComparison.NotEqual, V(2)), new ComparisonPredicate(column, SelectionComparison.GreaterThan, V(3)), new ComparisonPredicate(column, SelectionComparison.GreaterOrEqual, V(4)), new ComparisonPredicate(column, SelectionComparison.LessThan, V(5)), new ComparisonPredicate(column, SelectionComparison.LessOrEqual, V(6)), new BetweenPredicate(column, V(7), V(8)), new SetPredicate(column, false, [V(9)]), new SetPredicate(column, true, [V(10)]), new NullPredicate(new("o", "Name"), false), new NullPredicate(new("o", "Name"), true), new TextPredicate(new("o", "Name"), TextMatch.Contains, V("a")), new TextPredicate(new("o", "Name"), TextMatch.StartsWith, V("b")), new TextPredicate(new("o", "Name"), TextMatch.EndsWith, V("c")), new BooleanPredicate(new("o", "Active"), V(true)), new TemporalRangePredicate(new("o", "Day"), TemporalKind.Date, V(new DateOnly(2026, 9, 2)), V(new DateOnly(2026, 9, 3))), new TemporalRangePredicate(new("o", "At"), TemporalKind.Time, V(new TimeOnly(9, 0)), V(new TimeOnly(10, 0))), new TemporalRangePredicate(new("o", "Occurred"), TemporalKind.DateTime, V(new DateTime(2026, 9, 2)), V(new DateTime(2026, 9, 3))) }) yield return Query(predicate);
        var lines = new TableDefinition("sales", "Lines", [new("Id", typeof(int), false), new("OrderId", typeof(int), false)], new("PK_Lines", ["Id"]), []);
        yield return new(new([Orders, lines], []), new(Orders, "o"), new(Orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "Id"), "OrderId")], null, false));
        yield return new(new([Orders, lines], []), new(Orders, "o"), new(Orders.PrimaryKey), [], new ExistsPredicate(lines, "l", [new(new("o", "Id"), "OrderId")], null, true));
    }
}
```

- [ ] **Step 2: Run the focused tests and confirm the AST is absent.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionQueryValidationTests"`

Expected: compilation fails with CS0234 stating that namespace `DataPitcher.Core.Selection` does not exist. This is the intended red state; do not add the provider reference or generator yet.

- [ ] **Step 3: Add the complete Core model and validator.**

```csharp
// src/DataPitcher.Core/Selection/SelectionQueryModels.cs
using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Selection;
public enum SelectionComparison { Equal, NotEqual, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }
public enum TextMatch { Contains, StartsWith, EndsWith }
public enum TemporalKind { Date, Time, DateTime }
public enum RelationshipDirection { Forward, Reverse }
public sealed record SelectionParameterValue { public SelectionParameterValue(Type clrType, object value) { if (value is null || value.GetType() != clrType) throw new ArgumentException("Selection parameter value must have its declared non-null CLR type."); ClrType = clrType; Value = value; } public Type ClrType { get; } public object Value { get; } }
public sealed record SelectionSqlParameter(string Name, Type ClrType, object Value);
public sealed class GeneratedSelectionSql { public GeneratedSelectionSql(string commandText, TableDefinition rootTable, UniqueConstraint rootStableKey, IEnumerable<SelectionSqlParameter> parameters) { CommandText = commandText; RootTable = rootTable; RootStableKey = rootStableKey; Parameters = Array.AsReadOnly(parameters.ToArray()); } public string CommandText { get; } public TableDefinition RootTable { get; } public UniqueConstraint RootStableKey { get; } public IReadOnlyList<SelectionSqlParameter> Parameters { get; } }
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
```

```csharp
// src/DataPitcher.Core/Selection/SelectionQueryValidator.cs
using System.Text.RegularExpressions;
using DataPitcher.Core.Schema;
namespace DataPitcher.Core.Selection;
public static partial class SelectionQueryValidator
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex AliasPattern();
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
            case AndPredicate and when and.Terms.Count >= 2: Each(schema, and.Terms, aliases); return;
            case OrPredicate or when or.Terms.Count >= 2: Each(schema, or.Terms, aliases); return;
            case NotPredicate not: ValidatePredicate(schema, not.Term, aliases); return;
            case ComparisonPredicate comparison: Value(Column(aliases, comparison.Column), comparison.Value); return;
            case BetweenPredicate between: Values(Column(aliases, between.Column), between.Lower, between.Upper); return;
            case SetPredicate set when set.Values.Count > 0: EachValue(Column(aliases, set.Column), set.Values); return;
            case NullPredicate nullTest: Column(aliases, nullTest.Column); return;
            case TextPredicate text when Column(aliases, text.Column).ClrType == typeof(string): Value(Column(aliases, text.Column), text.Value); return;
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
```

`NOT` is valid for every predicate; operator checks still reject incompatible types. Top-level collections are copied before validation.

- [ ] **Step 4: Run the focused tests and confirm they pass.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionQueryValidationTests"`

Expected: the focused unit run passes; it exercises all required comparison, null, text, Boolean, temporal, set, `EXISTS`, `NOT EXISTS`, nested `AND`/`OR`/`NOT`, forward/reverse relationship path, and manual-join validation cases without Docker.

- [ ] **Step 5: Commit the Core AST.**

Run: `git add src/DataPitcher.Core/Selection/SelectionQueryModels.cs src/DataPitcher.Core/Selection/SelectionQueryValidator.cs tests/DataPitcher.UnitTests/Selection/SelectionQueryValidationTests.cs && git commit -m "feat: add typed selection query AST"`

### Task 2: Normalize and fingerprint equivalent queries

**Files:**
- Create: `src/DataPitcher.Core/Selection/SelectionQueryNormalizer.cs`, `src/DataPitcher.Core/Selection/SelectionQueryFingerprint.cs`, `tests/DataPitcher.UnitTests/Selection/SelectionQueryNormalizationTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Selection/SelectionQueryNormalizationTests.cs`

- [ ] **Step 1: Write the failing canonical-form and property-based tests.**

```csharp
using DataPitcher.Core.Selection;
using Xunit;
namespace DataPitcher.UnitTests.Selection;
public sealed class SelectionQueryNormalizationTests
{
    [Fact]
    public void Normalize_FlattensSortsDeduplicatesAndIsIdempotent()
    {
        var query = SelectionQueryTestData.Query(new AndPredicate([SelectionQueryTestData.Id(2), new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2)]) ]));
        var once = SelectionQueryNormalizer.Normalize(query); var twice = SelectionQueryNormalizer.Normalize(once);
        Assert.Equal(SelectionQueryFingerprint.CanonicalText(once), SelectionQueryFingerprint.CanonicalText(twice)); Assert.Contains("where(and(eq(o.Id:system.int32:1),eq(o.Id:system.int32:2)))", SelectionQueryFingerprint.CanonicalText(once));
    }
    [Fact]
    public void EquivalentBooleanTrees_HaveTheSameFingerprint()
    {
        var left = SelectionQueryTestData.Query(new OrPredicate([SelectionQueryTestData.Id(2), new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(3)])]));
        var right = SelectionQueryTestData.Query(new OrPredicate([new AndPredicate([SelectionQueryTestData.Id(3), SelectionQueryTestData.Id(1)]), SelectionQueryTestData.Id(2)]));
        Assert.Equal(SelectionQueryFingerprint.Sha256(left), SelectionQueryFingerprint.Sha256(right));
    }
    [Fact] public void MaterialPredicateChange_HasDifferentFingerprint() => Assert.NotEqual(SelectionQueryFingerprint.Sha256(SelectionQueryTestData.Query(SelectionQueryTestData.Id(1))), SelectionQueryFingerprint.Sha256(SelectionQueryTestData.Query(SelectionQueryTestData.Id(2))));
    [Fact]
    public void RandomlyPermutedConjunctions_AreIdempotentAndFingerprintEqually()
    {
        var random = new Random(20260902); var canonical = SelectionQueryFingerprint.Sha256(SelectionQueryTestData.Query(new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3)])));
        for (var i = 0; i < 128; i++) { var terms = new[] { SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3) }.OrderBy(_ => random.Next()).ToArray(); var query = SelectionQueryTestData.Query(new AndPredicate(terms)); var normalized = SelectionQueryNormalizer.Normalize(query); Assert.Equal(SelectionQueryFingerprint.CanonicalText(normalized), SelectionQueryFingerprint.CanonicalText(SelectionQueryNormalizer.Normalize(normalized))); Assert.Equal(canonical, SelectionQueryFingerprint.Sha256(query)); }
    }
}
```

- [ ] **Step 2: Run the tests and confirm the normalizer is absent.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionQueryNormalizationTests"`

Expected: compilation fails with CS0103 that `SelectionQueryNormalizer` and `SelectionQueryFingerprint` do not exist. The red state proves the property test is exercising the missing canonicalization behavior rather than an implementation detail.

- [ ] **Step 3: Add canonicalization and deterministic hashing.**

```csharp
// src/DataPitcher.Core/Selection/SelectionQueryNormalizer.cs
namespace DataPitcher.Core.Selection;
public static class SelectionQueryNormalizer
{
    public static SelectionQuery Normalize(SelectionQuery query) => new(query.Schema, query.Root, query.RootStableKey, query.Joins, Normalize(query.Predicate));
    public static SelectionPredicate? Normalize(SelectionPredicate? predicate) => predicate switch
    {
        null => null, NotPredicate(var term) when Normalize(term) is NotPredicate(var inner) => Normalize(inner), NotPredicate(var term) => new NotPredicate(Normalize(term)!),
        AndPredicate(var terms) => Boolean(true, terms), OrPredicate(var terms) => Boolean(false, terms), SetPredicate(var column, var negated, var values) => new SetPredicate(column, negated, values.Distinct().OrderBy(ValueKey, StringComparer.Ordinal).ToArray()),
        _ => predicate
    };
    private static SelectionPredicate Boolean(bool and, IReadOnlyList<SelectionPredicate> terms)
    {
        var flattened = terms.Select(Normalize).SelectMany(x => and && x is AndPredicate a ? a.Terms : !and && x is OrPredicate o ? o.Terms : [x!]).Distinct().OrderBy(SelectionQueryFingerprint.PredicateText, StringComparer.Ordinal).ToArray();
        return and ? new AndPredicate(flattened) : new OrPredicate(flattened);
    }
    internal static string ValueKey(SelectionParameterValue value) => value.ClrType.FullName + ":" + System.Text.Json.JsonSerializer.Serialize(value.Value, value.ClrType);
}
```

```csharp
// src/DataPitcher.Core/Selection/SelectionQueryFingerprint.cs
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
```

The seeded 128-case property test is reproducible without a package. Normalization flattens, sorts, and deduplicates same-kind Boolean nodes, removes double negation, and sorts set values; it avoids nullable-unsafe rewrites.

- [ ] **Step 4: Run the normalization tests and confirm they pass.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~SelectionQueryNormalizationTests"`

Expected: all three tests pass. The deterministic 128-case property run proves normalization idempotence and fingerprint equality for structurally equivalent conjunctions; changing a predicate value, operator, root, join, or stable-key column changes the canonical text and SHA-256 output.

- [ ] **Step 5: Commit normalization and fingerprinting.**

Run: `git add src/DataPitcher.Core/Selection/SelectionQueryNormalizer.cs src/DataPitcher.Core/Selection/SelectionQueryFingerprint.cs tests/DataPitcher.UnitTests/Selection/SelectionQueryNormalizationTests.cs && git commit -m "feat: normalize selection queries"`

### Task 3: Generate parameterized PostgreSQL root-key SQL

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs`, `tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs`
- Modify: `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`
- Test: `tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs`

- [ ] **Step 1: Write the failing compiler security and structure tests.**

```csharp
using DataPitcher.Core.Selection;
using DataPitcher.Providers.PostgreSql;
using Xunit;
namespace DataPitcher.UnitTests.Selection;
public sealed class PostgreSqlSelectionSqlGeneratorTests
{
    [Fact]
    public void Compile_ProjectsOnlyDistinctRootStableKeysAndQuotesEveryIdentifier()
    {
        var query = SelectionQueryTestData.QuotedRootAndJoin(); var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.Equal(query.Root.Table, sql.RootTable); Assert.Equal(query.RootStableKey.Constraint, sql.RootStableKey);
        Assert.StartsWith("SELECT DISTINCT \"r\".\"Id\" FROM \"sales\".\"Order\"\"Rows\" AS \"r\" INNER JOIN", sql.CommandText);
        Assert.Contains("\"r\".\"CustomerId\" = \"c\".\"Id\"", sql.CommandText); Assert.Contains("\"c\".\"RegionId\" = \"g\".\"Id\"", sql.CommandText); Assert.DoesNotContain("SELECT DISTINCT \"c\".\"Id\"", sql.CommandText);
    }
    [Fact]
    public void Compile_NeverInlinesSqlMetacharactersAndEscapesLikeAsAParameter()
    {
        const string attack = "x%' OR 1=1; DROP TABLE orders; --"; var query = SelectionQueryTestData.Query(new TextPredicate(new("o", "Name"), TextMatch.Contains, new(typeof(string), attack)));
        var sql = new PostgreSqlSelectionSqlGenerator().Compile(query);
        Assert.DoesNotContain(attack, sql.CommandText); Assert.Equal("@p0", sql.Parameters[0].Name); Assert.Equal(typeof(string), sql.Parameters[0].ClrType); Assert.Equal("x\\%' OR 1=1; DROP TABLE orders; --", sql.Parameters[0].Value); Assert.Contains("LIKE ('%' || @p0 || '%') ESCAPE '\\'", sql.CommandText);
    }
    [Fact]
    public void Compile_UsesPlaceholdersForEveryOperatorValue()
    {
        var generator = new PostgreSqlSelectionSqlGenerator();
        foreach (var query in SelectionQueryTestData.OperatorQueries()) { var sql = generator.Compile(query); Assert.All(sql.Parameters, parameter => Assert.Contains(parameter.Name, sql.CommandText)); }
    }
    [Fact]
    public void Compile_ReversesKnownRelationshipColumns()
    {
        var parent = new DataPitcher.Core.Schema.TableDefinition("sales", "Parents", [new("Id", typeof(int), false)], new("PK_Parents", ["Id"]), []); var child = new DataPitcher.Core.Schema.TableDefinition("sales", "Children", [new("Id", typeof(int), false), new("ParentId", typeof(int), false)], new("PK_Children", ["Id"]), []); var fk = new DataPitcher.Core.Schema.ForeignKeyDefinition("FK_Children_Parents", child, parent, ["ParentId"], ["Id"], true, true);
        var query = new SelectionQuery(new([parent, child], [fk]), new(parent, "p"), new(parent.PrimaryKey), [new ForeignKeyJoin("p", "c", fk, RelationshipDirection.Reverse)], null);
        Assert.Contains("\"p\".\"Id\" = \"c\".\"ParentId\"", new PostgreSqlSelectionSqlGenerator().Compile(query).CommandText);
    }
    [Fact]
    public void RandomlyEquivalentQueries_ProduceIdenticalSqlAndTypedParameterLists()
    {
        var random = new Random(20260902); var generator = new PostgreSqlSelectionSqlGenerator(); var expected = generator.Compile(SelectionQueryTestData.Query(new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3)])));
        for (var i = 0; i < 128; i++) { var terms = new[] { SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3) }.OrderBy(_ => random.Next()).ToArray(); var actual = generator.Compile(SelectionQueryTestData.Query(new AndPredicate(terms))); Assert.Equal(expected.CommandText, actual.CommandText); Assert.Equal(expected.Parameters, actual.Parameters); }
    }
}
```

- [ ] **Step 2: Run the compiler tests and confirm the provider reference/compiler is absent.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~PostgreSqlSelectionSqlGeneratorTests"`

Expected: compilation fails with CS0234 because `DataPitcher.Providers.PostgreSql` is not referenced by `DataPitcher.UnitTests`, and CS0246 because `PostgreSqlSelectionSqlGenerator` does not exist. This validates the test project has not accidentally acquired provider behavior before the pure compiler is introduced.

- [ ] **Step 3: Add the test-only project reference and closed PostgreSQL SQL writer.**

```xml
<!-- tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj: add inside ItemGroup -->
<ProjectReference Include="../../src/DataPitcher.Providers.PostgreSql/DataPitcher.Providers.PostgreSql.csproj" />
```

```csharp
using System.Text;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
namespace DataPitcher.Providers.PostgreSql;
public sealed class PostgreSqlSelectionSqlGenerator
{
    public GeneratedSelectionSql Compile(SelectionQuery source)
    {
        var query = SelectionQueryNormalizer.Normalize(source); var writer = new Writer(); writer.Token("SELECT DISTINCT "); Columns(writer, query.Root.Alias, query.RootStableKey.Constraint!.Columns); writer.Token(" FROM "); Table(writer, query.Root.Table); writer.Token(" AS "); writer.Identifier(query.Root.Alias);
        foreach (var join in query.Joins) Join(writer, join); if (query.Predicate is not null) { writer.Token(" WHERE "); Predicate(writer, query.Predicate); }
        return new GeneratedSelectionSql(writer.Text, query.Root.Table, query.RootStableKey.Constraint!, writer.Parameters);
    }
    private static void Join(Writer w, SelectionJoin join) { var (table, left, right) = join is ForeignKeyJoin f && f.Direction == RelationshipDirection.Forward ? (f.ForeignKey.ParentTable, f.ForeignKey.ChildColumns, f.ForeignKey.ParentColumns) : join is ForeignKeyJoin f ? (f.ForeignKey.ChildTable, f.ForeignKey.ParentColumns, f.ForeignKey.ChildColumns) : (((ManualJoin)join).Table, ((ManualJoin)join).Pairs.Select(x => x.FromColumn).ToArray(), ((ManualJoin)join).Pairs.Select(x => x.ToColumn).ToArray()); w.Token(" INNER JOIN "); Table(w, table); w.Token(" AS "); w.Identifier(join.Alias); w.Token(" ON "); Pairs(w, join.FromAlias, left, join.Alias, right); }
    private static void Predicate(Writer w, SelectionPredicate p) { switch (p) { case AndPredicate a: Group(w, " AND ", a.Terms); break; case OrPredicate o: Group(w, " OR ", o.Terms); break; case NotPredicate n: w.Token("NOT ("); Predicate(w, n.Term); w.Token(")"); break; case ComparisonPredicate c: Column(w, c.Column); w.Token(c.Operator switch { SelectionComparison.Equal => " = ", SelectionComparison.NotEqual => " <> ", SelectionComparison.GreaterThan => " > ", SelectionComparison.GreaterOrEqual => " >= ", SelectionComparison.LessThan => " < ", SelectionComparison.LessOrEqual => " <= ", _ => throw new ArgumentOutOfRangeException() }); w.Parameter(c.Value); break; case BetweenPredicate b: Column(w, b.Column); w.Token(" BETWEEN "); w.Parameter(b.Lower); w.Token(" AND "); w.Parameter(b.Upper); break; case SetPredicate s: Column(w, s.Column); w.Token(s.Negated ? " NOT IN (" : " IN ("); for (var i = 0; i < s.Values.Count; i++) { if (i > 0) w.Token(", "); w.Parameter(s.Values[i]); } w.Token(")"); break; case NullPredicate n: Column(w, n.Column); w.Token(n.Negated ? " IS NOT NULL" : " IS NULL"); break; case TextPredicate t: Column(w, t.Column); w.Token(t.Match switch { TextMatch.Contains => " LIKE ('%' || ", TextMatch.StartsWith => " LIKE (", TextMatch.EndsWith => " LIKE ('%' || ", _ => throw new ArgumentOutOfRangeException() }); w.Parameter(new(typeof(string), EscapeLike((string)t.Value.Value))); w.Token(t.Match switch { TextMatch.Contains => " || '%') ESCAPE '\\'", TextMatch.StartsWith => " || '%') ESCAPE '\\'", TextMatch.EndsWith => " || '%') ESCAPE '\\'", _ => throw new ArgumentOutOfRangeException() }); break; case BooleanPredicate b: Column(w, b.Column); w.Token(" = "); w.Parameter(b.Value); break; case TemporalRangePredicate r: Column(w, r.Column); w.Token(" BETWEEN "); w.Parameter(r.Lower); w.Token(" AND "); w.Parameter(r.Upper); break; case ExistsPredicate e: w.Token(e.Negated ? "NOT EXISTS (SELECT 1 FROM " : "EXISTS (SELECT 1 FROM "); Table(w, e.Table); w.Token(" AS "); w.Identifier(e.Alias); w.Token(" WHERE "); for (var i = 0; i < e.Correlations.Count; i++) { if (i > 0) w.Token(" AND "); Column(w, e.Correlations[i].OuterColumn); w.Token(" = "); w.Identifier(e.Alias); w.Token("."); w.Identifier(e.Correlations[i].InnerColumn); } if (e.Predicate is not null) { w.Token(" AND "); Predicate(w, e.Predicate); } w.Token(")"); break; default: throw new ArgumentOutOfRangeException(nameof(p)); } }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static void Group(Writer w, string token, IReadOnlyList<SelectionPredicate> terms) { w.Token("("); for (var i = 0; i < terms.Count; i++) { if (i > 0) w.Token(token); Predicate(w, terms[i]); } w.Token(")"); }
    private static void Columns(Writer w, string alias, IReadOnlyList<string> columns) { for (var i = 0; i < columns.Count; i++) { if (i > 0) w.Token(", "); w.Identifier(alias); w.Token("."); w.Identifier(columns[i]); } }
    private static void Pairs(Writer w, string leftAlias, IReadOnlyList<string> left, string rightAlias, IReadOnlyList<string> right) { for (var i = 0; i < left.Count; i++) { if (i > 0) w.Token(" AND "); w.Identifier(leftAlias); w.Token("."); w.Identifier(left[i]); w.Token(" = "); w.Identifier(rightAlias); w.Token("."); w.Identifier(right[i]); } }
    private static void Column(Writer w, SelectionColumn c) { w.Identifier(c.Alias); w.Token("."); w.Identifier(c.Name); }
    private static void Table(Writer w, TableDefinition t) { w.Identifier(t.Schema); w.Token("."); w.Identifier(t.Name); }
    private sealed class Writer { private readonly StringBuilder _text = new(); private readonly List<SelectionSqlParameter> _parameters = []; public string Text => _text.ToString(); public IReadOnlyList<SelectionSqlParameter> Parameters => _parameters; public void Token(string token) => _text.Append(token); public void Identifier(string identifier) => _text.Append(PostgreSqlIdentifier.Quote(identifier)); public void Parameter(SelectionParameterValue value) { var name = "@p" + _parameters.Count; _parameters.Add(new(name, value.ClrType, value.Value)); _text.Append(name); } }
}
```

`Writer.Token` accepts only compiler literals; `Writer.Identifier` is the sole identifier path and delegates to `PostgreSqlIdentifier.Quote`; `Writer.Parameter` records typed values without writing them into command text. Tests cover every operator branch, join direction, manual join, and an embedded identifier quote.

- [ ] **Step 4: Run the compiler tests and confirm they pass.**

Run: `./scripts/test-unit.sh --filter "FullyQualifiedName~PostgreSqlSelectionSqlGeneratorTests|FullyQualifiedName~SelectionQueryNormalizationTests"`

Expected: focused tests pass without Docker. Metacharacters appear only in escaped typed parameters, command text has generated `@pN` placeholders, quoted identifiers double embedded quotes, and root metadata/projection proves joins add no root. Equivalent ASTs produce identical SQL and parameter order.

- [ ] **Step 5: Run the complete Docker-free unit lane and commit the compiler.**

Run: `./scripts/test-unit.sh && git add tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj src/DataPitcher.Providers.PostgreSql/PostgreSqlSelectionSqlGenerator.cs tests/DataPitcher.UnitTests/Selection/PostgreSqlSelectionSqlGeneratorTests.cs && git commit -m "feat: generate postgres selection SQL"`

Expected: the Docker-free Core unit and architecture suites pass. Docker-capable CI subsequently runs the unchanged merged 100 percent `./scripts/test-all.sh` gate.

## Self-Review

Covered: all required operators, Boolean nesting, relationship/manual joins, typed parameters, quoted identifiers, one-root `DISTINCT` stable-key SQL, normalization/fingerprinting, and both required property tests.

Deferred: execution, preview, counts, persistence, sealing, raw SQL, SQL Server, outer joins, grouping, ordering, extra projection, and database integration. Aggregate 100 percent coverage remains exclusively in `scripts/test-all.sh`; this slice uses Docker-free `scripts/test-unit.sh`.

Consistency: checked all later type and method names against Task 1, including `SelectionQueryTestData`, normalizer/fingerprint APIs, compiler `Compile`, generated parameter models, quoter, predicates, and joins.
