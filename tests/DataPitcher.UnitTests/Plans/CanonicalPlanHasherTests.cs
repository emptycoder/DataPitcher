using System.Globalization;
using DataPitcher.Core.Plans;
using Xunit;

namespace DataPitcher.UnitTests.Plans;

public sealed class CanonicalPlanHasherTests
{
    [Fact]
    public void Hash_WhenEveryCollectionIsReversed_IsIdentical() =>
        Assert.Equal(
            CanonicalPlanHasher.Hash(PlanTestData.Baseline()),
            CanonicalPlanHasher.Hash(PlanTestData.Reversed())
        );

    [Fact]
    public void Hash_WhenTheSealingVersionDiffers_Differs() =>
        Assert.NotEqual(
            CanonicalPlanHasher.Hash(PlanTestData.Baseline()),
            CanonicalPlanHasher.Hash(PlanTestData.Baseline(sealingVersion: 0))
        );

    [Fact]
    public void Hash_WhenCurrentCultureIsSwedishOrTurkish_IsUnchanged()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var content = PlanTestData.CultureSensitive();
        var expected = CanonicalPlanHasher.Hash(content);
        try
        {
            foreach (var name in new[] { "sv-SE", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, CanonicalPlanHasher.Hash(content));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Hash_WhenTopologicalGroupMemberInputOrderDiffers_IsIdentical()
    {
        var first = PlanTestData.Baseline();
        var second = PlanTestData.Reversed();
        Assert.Equal(CanonicalPlanHasher.Hash(first), CanonicalPlanHasher.Hash(second));
    }

    [Fact]
    public void Hash_WhenConnectionIdDiffers_Changes()
    {
        var source = new ConnectionFingerprint(
            "PostgreSql",
            "source-db-001",
            "source-fingerprint",
            Guid.Parse("11111111-1111-1111-1111-111111111111")
        );
        Assert.NotEqual(
            CanonicalPlanHasher.Hash(PlanTestData.Baseline()),
            CanonicalPlanHasher.Hash(PlanTestData.Baseline(source: source))
        );
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 100).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Property_EquivalentCanonicalPlans_HashEqually(int seed)
    {
        var content = PlanTestData.Baseline();
        var shuffled = PlanTestData.Shuffled(seed);
        Assert.Equal(CanonicalPlanHasher.Hash(content), CanonicalPlanHasher.Hash(shuffled));
    }

    public static IEnumerable<object[]> MaterialChanges() =>
        new[]
        {
            "connection",
            "database identity",
            "schema snapshot",
            "target schema snapshot",
            "selection",
            "selection parameter",
            "stable key",
            "relationship policy",
            "relationship column order",
            "conflict policy",
            "column mapping",
            "transfer mode",
            "consistency mode",
            "trigger strategy",
            "constraint strategy",
        }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(MaterialChanges))]
    public void Property_AnyMaterialChange_ChangesHash(string material) =>
        Assert.NotEqual(
            CanonicalPlanHasher.Hash(PlanTestData.Baseline()),
            CanonicalPlanHasher.Hash(PlanTestData.Changed(material))
        );
}
