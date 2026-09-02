using DataPitcher.Core.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Identity;

public sealed class StableKeyByteArrayTests
{
    [Fact]
    public void StableKey_WhenValueIsByteArray_CanBeConstructed()
    {
        byte[] value = [1, 2, 3];
        var key = new StableKey([new("A", value)]);
        Assert.NotNull(key);
    }

    [Fact]
    public void StableKey_WhenByteArrayValuesAreStructurallyEqual_KeysAreEqual()
    {
        byte[] left = [1, 2, 3];
        byte[] right = [1, 2, 3];
        var leftKey = new StableKey([new("A", left)]);
        var rightKey = new StableKey([new("A", right)]);

        Assert.Equal(leftKey, rightKey);
        Assert.Equal(leftKey.GetHashCode(), rightKey.GetHashCode());
        Assert.Single(new HashSet<StableKey> { leftKey, rightKey });
    }

    [Fact]
    public void StableKey_WhenByteArrayValuesDiffer_OrdersDeterministically()
    {
        byte[] lowerValue = [1, 2, 3];
        byte[] higherValue = [1, 2, 4];
        var lower = new StableKey([new("A", lowerValue)]);
        var higher = new StableKey([new("A", higherValue)]);

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(lower) > 0);
    }
}
