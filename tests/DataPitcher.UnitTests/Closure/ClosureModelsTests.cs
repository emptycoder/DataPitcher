using DataPitcher.Core.Closure; using DataPitcher.Core.Identity; using DataPitcher.Core.Schema; using Xunit;
namespace DataPitcher.UnitTests.Closure;
public sealed class ClosureModelsTests
{
    [Fact] public void ClosureModels_WhenInputsAreMutated_ExposeDefensiveNonWritableCopies()
    {
        var table = new TableDefinition("dbo", "T", [], new UniqueConstraint("PK_T", ["K1"]), []); var key = new StableKey([new KeyComponent("K1", 1)]); var keys = new List<StableKey> { key }; var root = new ClosureRoot(table, keys, RootConflictPolicy.FailOnConflict); var roots = new List<ClosureRoot> { root }; var relationships = new List<ClosureRelationship> { ClosureRelationship.Manual("Manual", table, table) }; var selections = new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) };
        var request = new ClosureRequest(roots, relationships, selections); var rows = new List<ClosureRow> { new(table, key, 0) }; var warnings = new List<TargetConstraintWarning> { new("FK_Target") }; var result = new ClosureResult(rows, warnings); var constraints = new Dictionary<ClosureRelationship, TargetConstraintState> { [relationships[0]] = new("FK_Target",true,true,true) }; var probe = new TargetProbe(true,constraints);
        keys.Clear(); roots.Clear(); relationships.Clear(); selections.Clear(); rows.Clear(); warnings.Clear(); constraints.Clear();
        Assert.Single(root.Keys); Assert.Single(request.Roots); Assert.Single(request.Relationships); Assert.Single(request.StableKeySelections); Assert.Single(result.Rows); Assert.Single(result.Warnings); Assert.Single(probe.Constraints);
        Assert.Throws<NotSupportedException>(() => ((IList<StableKey>)root.Keys).Clear()); Assert.Throws<NotSupportedException>(() => ((IList<ClosureRoot>)request.Roots).Clear()); Assert.Throws<NotSupportedException>(() => ((IList<ClosureRelationship>)request.Relationships).Clear()); Assert.Throws<NotSupportedException>(() => ((IDictionary<TableDefinition, StableKeySelection>)request.StableKeySelections).Clear()); Assert.Throws<NotSupportedException>(() => ((IList<ClosureRow>)result.Rows).Clear()); Assert.Throws<NotSupportedException>(() => ((IList<TargetConstraintWarning>)result.Warnings).Clear()); Assert.Throws<NotSupportedException>(() => ((IDictionary<ClosureRelationship,TargetConstraintState>)probe.Constraints).Clear());
    }
}
