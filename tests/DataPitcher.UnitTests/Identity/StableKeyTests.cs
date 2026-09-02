using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Identity;

public sealed class StableKeyTests
{
    [Fact] public void StableKey_WhenSameColumnsAndValues_AreEqual()
    {
        var left = new StableKey([new("A", 1), new("B", "x")]);
        var right = new StableKey([new("A", 1), new("B", "x")]);
        Assert.Equal(left, right);
    }

    [Fact] public void StableKey_WhenColumnOrderDiffers_AreNotEqual()
    {
        var left = new StableKey([new("A", 1), new("B", 2)]);
        var right = new StableKey([new("B", 2), new("A", 1)]);
        Assert.NotEqual(left, right);
    }

    [Fact] public void StableKey_WhenValueIsNull_ComparesConsistently()
    {
        var nullKey = new StableKey([new("A", null)]);
        var valueKey = new StableKey([new("A", 1)]);
        Assert.Equal(0, nullKey.CompareTo(new StableKey([new("A", null)])));
        Assert.True(nullKey.CompareTo(valueKey) < 0);
    }

    [Fact] public void StableKey_WhenSorted_ProducesDeterministicTotalOrder()
    {
        var keys = new List<StableKey> { new([new("A", 2)]), new([new("B", 0)]), new([new("A", null)]), new([new("A", 1)]) };
        keys.Sort();
        Assert.Equal([new StableKey([new("A", null)]), new StableKey([new("A", 1)]), new StableKey([new("A", 2)]), new StableKey([new("B", 0)])], keys);
    }

    [Fact] public void StableKey_WhenComponentListIsEmpty_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new StableKey(Array.Empty<KeyComponent>()));
    }
}
