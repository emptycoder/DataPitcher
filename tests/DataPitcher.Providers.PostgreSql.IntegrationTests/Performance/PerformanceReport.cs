using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests.Performance;

/// <summary>
/// Collects phase timings for one scenario, prints them, appends them as one JSON line to the results file
/// (<c>DATAPITCHER_PERF_RESULTS</c>, default <c>artifacts/performance/results.jsonl</c> under the repository), and
/// enforces the per-phase budget (<c>DATAPITCHER_PERF_BUDGET_SECONDS</c>, default 120).
/// </summary>
public sealed class PerformanceReport(ITestOutputHelper output, string provider, string scenario)
{
    private readonly Dictionary<string, object> _metrics = new(StringComparer.Ordinal);
    private readonly List<(string Phase, TimeSpan Duration)> _phases = [];

    public static int Rows =>
        int.TryParse(Environment.GetEnvironmentVariable("DATAPITCHER_PERF_ROWS"), out var rows) && rows > 0
            ? rows
            : 20_000;

    public static TimeSpan Budget =>
        double.TryParse(
            Environment.GetEnvironmentVariable("DATAPITCHER_PERF_BUDGET_SECONDS"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var seconds
        )
        && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(120);

    public void Metric(string name, object value) => _metrics[name] = value;

    public async Task<T> TimeAsync<T>(string phase, Func<Task<T>> action)
    {
        var watch = Stopwatch.StartNew();
        var result = await action();
        watch.Stop();
        _phases.Add((phase, watch.Elapsed));
        output.WriteLine($"[{provider}] {scenario}: {phase} took {watch.Elapsed.TotalMilliseconds:F0} ms");
        return result;
    }

    public Task TimeAsync(string phase, Func<Task> action) =>
        TimeAsync(
            phase,
            async () =>
            {
                await action();
                return 0;
            }
        );

    /// <summary>Writes the record, then fails the test if any phase exceeded the budget.</summary>
    public async Task CompleteAsync()
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["provider"] = provider,
            ["scenario"] = scenario,
            ["machine"] = Environment.MachineName,
        };
        foreach (var (phase, duration) in _phases)
            record[phase + "Ms"] = Math.Round(duration.TotalMilliseconds, 1);
        foreach (var (name, value) in _metrics)
            record[name] = value;
        var path = ResultsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record) + Environment.NewLine);
        output.WriteLine($"[{provider}] {scenario}: results appended to {path}");
        var slow = _phases.Where(item => item.Duration > Budget).ToArray();
        if (slow.Length > 0)
            throw new Xunit.Sdk.XunitException(
                "Over budget ("
                    + Budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " s): "
                    + string.Join(
                        ", ",
                        slow.Select(item =>
                            item.Phase
                            + " "
                            + item.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                            + " s"
                        )
                    )
            );
    }

    private static string ResultsPath()
    {
        var configured = Environment.GetEnvironmentVariable("DATAPITCHER_PERF_RESULTS");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataPitcher.sln")))
            directory = directory.Parent;
        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "artifacts",
            "performance",
            "results.jsonl"
        );
    }
}
