using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests.Performance;

/// <summary>
/// A timing test that only runs when <c>DATAPITCHER_PERF=1</c> is set (see scripts/test-performance.sh), so the
/// regular suites stay fast. Results are appended to a JSON-lines file for comparison between runs.
/// </summary>
public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DATAPITCHER_PERF") is not "1")
            Skip = "Set DATAPITCHER_PERF=1 to run performance tests.";
    }
}
