using DataPitcher.Core.Selection;
using Xunit;

namespace DataPitcher.UnitTests.Selection;

public sealed class SelectionQueryNormalizationTests
{
    [Fact]
    public void Normalize_FlattensSortsDeduplicatesAndIsIdempotent()
    {
        var query = SelectionQueryTestData.Query(
            new AndPredicate([
                SelectionQueryTestData.Id(2),
                new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2)]),
            ])
        );
        var once = SelectionQueryNormalizer.Normalize(query);
        var twice = SelectionQueryNormalizer.Normalize(once);
        Assert.Equal(SelectionQueryFingerprint.CanonicalText(once), SelectionQueryFingerprint.CanonicalText(twice));
        Assert.Contains(
            "where(and(eq(o.Id:system.int32:1),eq(o.Id:system.int32:2)))",
            SelectionQueryFingerprint.CanonicalText(once)
        );
    }

    [Fact]
    public void EquivalentBooleanTrees_HaveTheSameFingerprint()
    {
        var left = SelectionQueryTestData.Query(
            new OrPredicate([
                SelectionQueryTestData.Id(2),
                new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(3)]),
            ])
        );
        var right = SelectionQueryTestData.Query(
            new OrPredicate([
                new AndPredicate([SelectionQueryTestData.Id(3), SelectionQueryTestData.Id(1)]),
                SelectionQueryTestData.Id(2),
            ])
        );
        Assert.Equal(SelectionQueryFingerprint.Sha256(left), SelectionQueryFingerprint.Sha256(right));
    }

    [Fact]
    public void MaterialPredicateChange_HasDifferentFingerprint() =>
        Assert.NotEqual(
            SelectionQueryFingerprint.Sha256(SelectionQueryTestData.Query(SelectionQueryTestData.Id(1))),
            SelectionQueryFingerprint.Sha256(SelectionQueryTestData.Query(SelectionQueryTestData.Id(2)))
        );

    [Fact]
    public void RandomlyPermutedConjunctions_AreIdempotentAndFingerprintEqually()
    {
        var random = new Random(20260902);
        var canonical = SelectionQueryFingerprint.Sha256(
            SelectionQueryTestData.Query(
                new AndPredicate([
                    SelectionQueryTestData.Id(1),
                    SelectionQueryTestData.Id(2),
                    SelectionQueryTestData.Id(3),
                ])
            )
        );
        for (var i = 0; i < 128; i++)
        {
            var terms = new[]
            {
                SelectionQueryTestData.Id(1),
                SelectionQueryTestData.Id(2),
                SelectionQueryTestData.Id(3),
            }
                .OrderBy(_ => random.Next())
                .ToArray();
            var query = SelectionQueryTestData.Query(new AndPredicate(terms));
            var normalized = SelectionQueryNormalizer.Normalize(query);
            Assert.Equal(
                SelectionQueryFingerprint.CanonicalText(normalized),
                SelectionQueryFingerprint.CanonicalText(SelectionQueryNormalizer.Normalize(normalized))
            );
            Assert.Equal(canonical, SelectionQueryFingerprint.Sha256(query));
        }
    }

    [Fact]
    public void Fingerprint_IsUnchangedUnderNonInvariantCultures()
    {
        var query = SelectionQueryTestData.Query(
            new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2)])
        );
        var invariant = SelectionQueryFingerprint.Sha256(query);
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var turkish = SelectionQueryFingerprint.Sha256(query);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            var swedish = SelectionQueryFingerprint.Sha256(query);
            Assert.Equal(invariant, turkish);
            Assert.Equal(invariant, swedish);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Fingerprint_ChangesWhenJoinIsAdded()
    {
        var joined = SelectionQueryTestData.QuotedRootAndJoin();
        var withoutJoin = new SelectionQuery(joined.Schema, joined.Root, joined.RootStableKey, [], null);
        Assert.NotEqual(SelectionQueryFingerprint.Sha256(withoutJoin), SelectionQueryFingerprint.Sha256(joined));
    }

    [Theory]
    [InlineData(SelectionComparison.Equal, "eq")]
    [InlineData(SelectionComparison.NotEqual, "ne")]
    [InlineData(SelectionComparison.GreaterThan, "gt")]
    [InlineData(SelectionComparison.GreaterOrEqual, "ge")]
    [InlineData(SelectionComparison.LessThan, "lt")]
    [InlineData(SelectionComparison.LessOrEqual, "le")]
    public void Fingerprint_IdentifiesComparisonOperator(SelectionComparison comparison, string name)
    {
        var query = SelectionQueryTestData.Query(
            new ComparisonPredicate(new("o", "Id"), comparison, new(typeof(int), 1))
        );
        Assert.Contains("where(" + name + "(o.Id:system.int32:1))", SelectionQueryFingerprint.CanonicalText(query));
    }

    [Fact]
    public void Fingerprint_DistinguishesNullPredicates()
    {
        var isNull = SelectionQueryFingerprint.CanonicalText(
            SelectionQueryTestData.Query(new NullPredicate(new("o", "Name"), false))
        );
        var isNotNull = SelectionQueryFingerprint.CanonicalText(
            SelectionQueryTestData.Query(new NullPredicate(new("o", "Name"), true))
        );
        Assert.Contains("where(null(o.Name))", isNull);
        Assert.Contains("where(notnull(o.Name))", isNotNull);
        Assert.NotEqual(isNull, isNotNull);
    }

    [Theory]
    [InlineData(TextMatch.Contains, "contains")]
    [InlineData(TextMatch.StartsWith, "startswith")]
    [InlineData(TextMatch.EndsWith, "endswith")]
    public void Fingerprint_IdentifiesTextMatch(TextMatch match, string name)
    {
        var query = SelectionQueryTestData.Query(
            new TextPredicate(new("o", "Name"), match, new(typeof(string), "match"))
        );
        Assert.Contains(
            "where(" + name + "(o.Name:system.string:\"match\"))",
            SelectionQueryFingerprint.CanonicalText(query)
        );
    }

    [Fact]
    public void Fingerprint_IdentifiesBooleanPredicate()
    {
        var query = SelectionQueryTestData.Query(new BooleanPredicate(new("o", "Active"), new(typeof(bool), true)));
        Assert.Contains("where(bool(o.Active:system.boolean:true))", SelectionQueryFingerprint.CanonicalText(query));
    }

    [Fact]
    public void Fingerprint_IdentifiesTemporalRangePredicate()
    {
        var query = SelectionQueryTestData.Query(
            new TemporalRangePredicate(
                new("o", "Day"),
                TemporalKind.Date,
                new(typeof(DateOnly), new DateOnly(2026, 9, 2)),
                new(typeof(DateOnly), new DateOnly(2026, 9, 3))
            )
        );
        Assert.Contains(
            "where(date(o.Day:system.dateonly:\"2026-09-02\":system.dateonly:\"2026-09-03\"))",
            SelectionQueryFingerprint.CanonicalText(query)
        );
    }

    [Fact]
    public void Fingerprint_DistinguishesExistsNegation()
    {
        var queries = SelectionQueryTestData.OperatorQueries().Where(x => x.Predicate is ExistsPredicate).ToArray();
        var exists = SelectionQueryFingerprint.CanonicalText(
            queries.Single(x => !((ExistsPredicate)x.Predicate!).Negated)
        );
        var notExists = SelectionQueryFingerprint.CanonicalText(
            queries.Single(x => ((ExistsPredicate)x.Predicate!).Negated)
        );
        Assert.Contains("where(exists(", exists);
        Assert.Contains("where(notexists(", notExists);
        Assert.NotEqual(exists, notExists);
    }

    [Fact]
    public void Normalize_CollapsesDoubleNegation()
    {
        var normalized = SelectionQueryNormalizer.Normalize(
            SelectionQueryTestData.Query(new NotPredicate(new NotPredicate(SelectionQueryTestData.Id(1))))
        );
        Assert.Equal(SelectionQueryTestData.Id(1), normalized.Predicate);
    }

    [Fact]
    public void Normalize_SortsAndDeduplicatesSetValues()
    {
        var query = SelectionQueryTestData.Query(
            new SetPredicate(new("o", "Id"), false, [new(typeof(int), 2), new(typeof(int), 1), new(typeof(int), 2)])
        );
        var normalized = Assert.IsType<SetPredicate>(SelectionQueryNormalizer.Normalize(query).Predicate);
        Assert.Equal([1, 2], normalized.Values.Select(x => (int)x.Value));
        Assert.Contains(
            "where(in(o.Id:system.int32:1,system.int32:2))",
            SelectionQueryFingerprint.CanonicalText(SelectionQueryTestData.Query(normalized))
        );
    }

    [Fact]
    public void Fingerprint_RepresentsNegation()
    {
        var query = SelectionQueryTestData.Query(new NotPredicate(SelectionQueryTestData.Id(1)));
        Assert.Contains("where(not(eq(o.Id:system.int32:1)))", SelectionQueryFingerprint.CanonicalText(query));
    }

    [Fact]
    public void Fingerprint_RepresentsBetweenRange()
    {
        var query = SelectionQueryTestData.Query(
            new BetweenPredicate(new("o", "Id"), new(typeof(int), 1), new(typeof(int), 2))
        );
        Assert.Contains(
            "where(between(o.Id:system.int32:1:system.int32:2))",
            SelectionQueryFingerprint.CanonicalText(query)
        );
    }

    [Fact]
    public void Fingerprint_DistinguishesSetNegation()
    {
        var included = SelectionQueryFingerprint.CanonicalText(
            SelectionQueryTestData.Query(new SetPredicate(new("o", "Id"), false, [new(typeof(int), 1)]))
        );
        var excluded = SelectionQueryFingerprint.CanonicalText(
            SelectionQueryTestData.Query(new SetPredicate(new("o", "Id"), true, [new(typeof(int), 1)]))
        );
        Assert.Contains("where(in(o.Id:system.int32:1))", included);
        Assert.Contains("where(notin(o.Id:system.int32:1))", excluded);
        Assert.NotEqual(included, excluded);
    }

    [Fact]
    public void Normalize_FlattensNestedDisjunction()
    {
        var normalized = Assert.IsType<OrPredicate>(
            SelectionQueryNormalizer
                .Normalize(
                    SelectionQueryTestData.Query(
                        new OrPredicate([
                            SelectionQueryTestData.Id(1),
                            new OrPredicate([SelectionQueryTestData.Id(2), SelectionQueryTestData.Id(3)]),
                        ])
                    )
                )
                .Predicate
        );
        Assert.Equal(3, normalized.Terms.Count);
    }

    [Fact]
    public void Fingerprint_RejectsUnknownPredicateWhenSelectionInvariantIsBypassed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionQueryFingerprint.CanonicalText(
                SelectionQueryTestData.WithUnvalidatedPredicate(new UnknownSelectionPredicate())
            )
        );
    }

    [Fact]
    public void Fingerprint_RejectsUndefinedComparisonOperatorWhenSelectionInvariantIsBypassed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionQueryFingerprint.CanonicalText(
                SelectionQueryTestData.WithUnvalidatedPredicate(
                    new ComparisonPredicate(new("o", "Id"), (SelectionComparison)99, new(typeof(int), 1))
                )
            )
        );
    }
}
