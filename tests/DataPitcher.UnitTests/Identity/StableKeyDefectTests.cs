using System.Globalization;
using System.Linq;
using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Identity;

public sealed class StableKeyDefectTests
{
    [Fact]
    public void StableKey_WhenSortingStringValues_OrderIsOrdinalAndCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            string[] values = ["apple", "Banana", "cherry"];
            var expectedOrdinal = values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantOrder = SortUnderCurrentCulture(values);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var svOrder = SortUnderCurrentCulture(values);

            Assert.Equal(expectedOrdinal, invariantOrder);
            Assert.Equal(expectedOrdinal, svOrder);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static string[] SortUnderCurrentCulture(string[] values)
    {
        var keys = values.Select(v => new StableKey([new("A", v)])).ToList();
        keys.Sort();
        return keys.Select(k => (string)k.Components[0].Value!).ToArray();
    }

    public static IEnumerable<object[]> EqualValuePairs()
    {
        yield return new object[] { 1.0m, 1.00m };
        yield return new object[]
        {
            new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero),
        };
        yield return new object[] { 0.0, -0.0 };
    }

    [Theory]
    [MemberData(nameof(EqualValuePairs))]
    public void StableKey_WhenValuesAreEqual_CompareToReturnsZero(IComparable left, IComparable right)
    {
        var leftKey = new StableKey([new("A", left)]);
        var rightKey = new StableKey([new("A", right)]);

        Assert.True(leftKey.Equals(rightKey));
        Assert.Equal(0, leftKey.CompareTo(rightKey));

        var hashSet = new HashSet<StableKey> { leftKey, rightKey };
        var sortedSet = new SortedSet<StableKey> { leftKey, rightKey };
        Assert.Equal(hashSet.Count, sortedSet.Count);
    }
}
