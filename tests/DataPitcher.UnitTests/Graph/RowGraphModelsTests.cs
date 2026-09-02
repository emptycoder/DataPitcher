using DataPitcher.Core.Closure;
using DataPitcher.Core.Graph;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Graph;

public sealed class RowGraphModelsTests
{
    [Fact]
    public void RowGraphRequest_WhenInputsAreMutated_ExposesImmutableRowsAndReferences()
    {
        var employees = Table("Employees"); var fk = ForeignKey(employees, employees);
        var manager = Row(employees, 1); var employee = Row(employees, 2);
        var rows = new List<RowAddress> { manager, employee };
        var references = new List<RowReference>
        {
            new(manager, fk, null, RowReferenceState.NullParent),
            new(employee, fk, manager, RowReferenceState.Planned),
        };
        var request = new RowGraphRequest(rows, references, new(10, TimeSpan.FromSeconds(5), 1_000_000));
        rows.Clear(); references.Clear();
        Assert.Equal(2, request.PlannedRows.Count); Assert.Equal(2, request.References.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<RowAddress>)request.PlannedRows).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RowReference>)request.References).Clear());
    }

    [Fact]
    public void RowGraphRequest_WhenCreated_PreservesAnalyzerLimits()
    {
        var limits = new RowGraphLimits(10, TimeSpan.FromSeconds(5), 1_000_000);
        var request = new RowGraphRequest([], [], limits);
        Assert.Equal(limits, request.Limits);
    }

    [Fact]
    public void RowGraphAnalysis_WhenExternalParentIsMissing_ReportsItSeparatelyFromAcyclicOrder()
    {
        var employees = Table("Employees"); var fk = ForeignKey(employees, employees);
        var employee = Row(employees, 2); var absentManager = Row(employees, 99);
        var analysis = new RowGraphAnalysis([employee], [], [new(employee, fk, absentManager)]);
        Assert.True(analysis.IsAcyclic); Assert.Empty(analysis.UnreachedRows);
        Assert.Equal(new MissingReference(employee, fk, absentManager), Assert.Single(analysis.MissingReferences));
    }

    private static TableDefinition Table(string name) => new("dbo", name, [], new($"PK_{name}", ["Id"]), []);
    private static ForeignKeyDefinition ForeignKey(TableDefinition child, TableDefinition parent) => new($"FK_{child.Name}_{parent.Name}", child, parent, ["ManagerId"], ["Id"], true, true);
    private static RowAddress Row(TableDefinition table, int id) => new(table, new StableKey([new("Id", id)]));
}
