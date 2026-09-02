using DataPitcher.Infrastructure.Time;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class SystemClockTests
{
    [Fact]
    public void SystemClock_WhenRead_ReturnsAdvancingUtcValues()
    {
        var clock = new SystemClock();
        var first = clock.UtcNow;

        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.True(SpinWait.SpinUntil(() => clock.UtcNow > first, TimeSpan.FromSeconds(1)));
    }
}
