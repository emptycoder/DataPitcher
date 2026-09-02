using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerProbeBatchingTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ComputeAsync_SendsOneTargetProbePerFrontierTableNotPerKey()
    {
        await using var scenario = await SqlServerClosureScenario.CreateAsync(fixture);
        await using var wire = await SqlServerWireCommandRecorder.StartAsync(scenario.TargetAdminConnectionString, "DataPitcher.ProbeTarget");
        await scenario.CreateBatchChainAsync(40);
        await scenario.RunBatchAsync();
        Assert.Equal(1, await wire.Count("DataPitcher.ProbeTarget", "batch_child"));
        Assert.Equal(1, await wire.Count("DataPitcher.ProbeTarget", "batch_parent"));
        Assert.Equal(2, await wire.Count("DataPitcher.ProbeTarget"));
        Assert.False(await wire.AnyContainsLargeInListAsync(10));
    }
}
