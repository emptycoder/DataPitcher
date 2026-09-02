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
    [Fact]
    public void Fingerprint_IsUnchangedUnderNonInvariantCultures()
    {
        var query = SelectionQueryTestData.Query(new AndPredicate([SelectionQueryTestData.Id(1), SelectionQueryTestData.Id(2)]));
        var invariant = SelectionQueryFingerprint.Sha256(query);
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var turkish = SelectionQueryFingerprint.Sha256(query);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            var swedish = SelectionQueryFingerprint.Sha256(query);
            Assert.Equal(invariant, turkish); Assert.Equal(invariant, swedish);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }
}
