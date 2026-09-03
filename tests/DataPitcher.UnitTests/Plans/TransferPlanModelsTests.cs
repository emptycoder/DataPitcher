using System.Text.Json;
using DataPitcher.Core.Plans;
using Xunit;
namespace DataPitcher.UnitTests.Plans;
public sealed class TransferPlanModelsTests
{
    [Fact]
    public void TransferPlanContent_WhenSourceCollectionsChange_RetainsIndependentValues()
    {
        var columns = new List<ColumnMapping> { new("Id", "Id") };
        var group = new List<TableAddress> { PlanTestData.Customers };
        var table = new PlanTable(new(PlanTestData.Customers, PlanTestData.Customers, columns), PlanTableState.Root, new(1, 1, 0, 0), new(group), CycleStrategy.NotApplicable);
        var tables = new List<PlanTable> { table };
        var content = PlanTestData.Baseline(tables: tables);
        columns[0] = new("Id", "Changed"); group[0] = PlanTestData.Orders; tables[0] = PlanTestData.Table(PlanTestData.Orders, PlanTableState.Conflict);
        Assert.Equal("Id", content.Tables[0].Mapping.Columns[0].Target);
        Assert.Equal(PlanTestData.Customers, content.Tables[0].TopologicalGroup.Tables[0]);
        Assert.Equal(PlanTableState.Root, content.Tables[0].State);
    }
    [Fact]
    public void TransferPlanContent_WhenExposedCollectionsAreDowncast_RejectsIndexerAssignment()
    {
        var content = PlanTestData.Baseline(); var table = content.Tables[0];
        Assert.Throws<NotSupportedException>(() => ((IList<PlanTable>)content.Tables)[0] = table);
        Assert.Throws<NotSupportedException>(() => ((IList<ColumnMapping>)table.Mapping.Columns)[0] = new("Id", "Changed"));
        Assert.Throws<NotSupportedException>(() => ((IList<TableAddress>)table.TopologicalGroup.Tables)[0] = PlanTestData.Orders);
        Assert.Throws<NotSupportedException>(() => ((IList<SelectionReference>)content.Selections)[0] = content.Selections[0]);
    }
    [Fact]
    public void TransferPlanContent_WhenSerialized_RoundTrips()
    {
        var expected = JsonSerializer.Serialize(PlanTestData.Baseline());
        var actual = JsonSerializer.Deserialize<TransferPlanContent>(expected);
        Assert.Equal(expected, JsonSerializer.Serialize(actual));
    }
    [Theory]
    [InlineData(PlanTableState.Root)] [InlineData(PlanTableState.RequiredDependency)]
    [InlineData(PlanTableState.ExplicitDependent)] [InlineData(PlanTableState.TargetSatisfied)]
    [InlineData(PlanTableState.Excluded)] [InlineData(PlanTableState.Blocked)]
    [InlineData(PlanTableState.Conflict)] [InlineData(PlanTableState.CycleMember)]
    public void PlanTable_RecordsEveryDefinedState(PlanTableState state) => Assert.Equal(state, PlanTestData.Table(PlanTestData.Orders, state).State);
}
