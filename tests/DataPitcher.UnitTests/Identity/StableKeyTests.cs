using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Identity;

public sealed class StableKeyTests
{
    [Fact]
    public void StableKey_WhenSameColumnsAndValues_AreEqual()
    {
        var left = new StableKey([new("A", 1), new("B", "x")]);
        var right = new StableKey([new("A", 1), new("B", "x")]);
        Assert.Equal(left, right);
    }

    [Fact]
    public void StableKey_WhenColumnOrderDiffers_AreNotEqual()
    {
        var left = new StableKey([new("A", 1), new("B", 2)]);
        var right = new StableKey([new("B", 2), new("A", 1)]);
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void StableKey_WhenValueIsNull_ComparesConsistently()
    {
        var nullKey = new StableKey([new("A", null)]);
        var valueKey = new StableKey([new("A", 1)]);
        Assert.Equal(0, nullKey.CompareTo(new StableKey([new("A", null)])));
        Assert.True(nullKey.CompareTo(valueKey) < 0);
    }

    [Fact]
    public void StableKey_WhenSorted_ProducesDeterministicTotalOrder()
    {
        var keys = new List<StableKey>
        {
            new([new("A", 2)]),
            new([new("B", 0)]),
            new([new("A", null)]),
            new([new("A", 1)]),
        };
        keys.Sort();
        Assert.Equal(
            [
                new StableKey([new("A", null)]),
                new StableKey([new("A", 1)]),
                new StableKey([new("A", 2)]),
                new StableKey([new("B", 0)]),
            ],
            keys
        );
    }

    [Fact]
    public void StableKey_WhenComponentListIsEmpty_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new StableKey(Array.Empty<KeyComponent>()));
    }

    [Fact]
    public void StableKey_Equals_WhenOtherIsNull_ReturnsFalse()
    {
        var key = new StableKey([new("A", 1)]);
        Assert.False(key.Equals((StableKey?)null));
    }

    [Fact]
    public void StableKey_EqualsObject_WhenSameValue_AgreesWithTypedEquals()
    {
        var left = new StableKey([new("A", 1)]);
        var right = new StableKey([new("A", 1)]);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.Equals(right), left.Equals((object)right));
    }

    [Fact]
    public void StableKey_EqualsObject_WhenComparedToNullOrUnrelatedType_ReturnsFalse()
    {
        var key = new StableKey([new("A", 1)]);
        Assert.False(key.Equals((object?)null));
        Assert.False(key.Equals("not a stable key"));
    }

    [Fact]
    public void StableKey_EqualityOperators_WhenValuesEqual_ReturnTrueAndFalse()
    {
        var left = new StableKey([new("A", 1)]);
        var right = new StableKey([new("A", 1)]);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact]
    public void StableKey_EqualityOperators_WhenValuesDiffer_ReturnFalseAndTrue()
    {
        var left = new StableKey([new("A", 1)]);
        var right = new StableKey([new("A", 2)]);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    [Fact]
    public void StableKey_EqualityOperator_WhenBothSidesAreNull_ReturnsTrue()
    {
        StableKey? left = null;
        StableKey? right = null;
        Assert.True(left == right);
    }

    [Fact]
    public void StableKey_EqualityOperator_WhenOnlyOneSideIsNull_ReturnsFalse()
    {
        StableKey? nullKey = null;
        var actualKey = new StableKey([new("A", 1)]);
        Assert.False(nullKey == actualKey);
        Assert.False(actualKey == nullKey);
    }

    [Fact]
    public void StableKey_CompareTo_WhenOtherIsNull_ReturnsPositive()
    {
        var key = new StableKey([new("A", 1)]);
        Assert.True(key.CompareTo(null) > 0);
    }

    [Fact]
    public void StableKey_CompareTo_WhenValueRuntimeTypesDiffer_OrdersConsistentlyAndDisagreesWithEquality()
    {
        var intKey = new StableKey([new("A", 1)]);
        var stringKey = new StableKey([new("A", "1")]);

        Assert.NotEqual(intKey, stringKey);
        var forward = intKey.CompareTo(stringKey);
        var backward = stringKey.CompareTo(intKey);
        Assert.NotEqual(0, forward);
        Assert.Equal(Math.Sign(forward), -Math.Sign(backward));
    }
}
