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

    [Fact] public void ClosureRelationship_WhenAllFieldsMatch_AreEqualAndProduceEqualHashCodes()
    {
        var c = T("C"); var p = T("P");
        var left = ClosureRelationship.Manual("Name", c, p);
        var right = ClosureRelationship.Manual("Name", c, p);
        Assert.Equal(left, right);
        Assert.True(left.Equals(right)); Assert.True(right.Equals(left));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
    [Fact] public void ClosureRelationship_WhenNameDiffers_AreNotEqual()
    {
        var c = T("C"); var p = T("P");
        Assert.NotEqual(ClosureRelationship.Manual("A", c, p), ClosureRelationship.Manual("B", c, p));
    }
    [Fact] public void ClosureRelationship_WhenFromTableDiffers_AreNotEqual()
    {
        var p = T("P");
        Assert.NotEqual(ClosureRelationship.Manual("N", T("C1"), p), ClosureRelationship.Manual("N", T("C2"), p));
    }
    [Fact] public void ClosureRelationship_WhenToTableDiffers_AreNotEqual()
    {
        var c = T("C");
        Assert.NotEqual(ClosureRelationship.Manual("N", c, T("P1")), ClosureRelationship.Manual("N", c, T("P2")));
    }
    [Fact] public void ClosureRelationship_WhenIsEnabledDiffers_AreNotEqual()
    {
        var c = T("C"); var p = T("P");
        Assert.NotEqual(ClosureRelationship.Manual("N", c, p, true), ClosureRelationship.Manual("N", c, p, false));
    }
    [Fact] public void ClosureRelationship_WhenIsInboundDiffers_AreNotEqual()
    {
        var self = T("T");
        var fk = new ForeignKeyDefinition("FK_Self", self, self, ["Id"], ["Id"], true, true);
        var outbound = new ClosureRelationship(fk, isInbound: false);
        var inbound = new ClosureRelationship(fk, isInbound: true);
        Assert.Equal(outbound.FromTable, inbound.FromTable);
        Assert.Equal(outbound.ToTable, inbound.ToTable);
        Assert.NotEqual(outbound, inbound);
    }
    [Fact] public void ClosureRelationship_Equals_WhenComparedToNullOrUnrelatedType_ReturnsFalse()
    {
        var relationship = ClosureRelationship.Manual("N", T("C"), T("P"));
        Assert.False(relationship.Equals((object?)null));
        Assert.False(relationship.Equals("not a relationship"));
    }
    [Fact] public void ClosureRelationship_EqualsObject_WhenSameValue_AgreesWithTypedEquals()
    {
        var c = T("C"); var p = T("P");
        var left = ClosureRelationship.Manual("N", c, p);
        var right = ClosureRelationship.Manual("N", c, p);
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.Equals(right), left.Equals((object)right));
    }

    private static TableDefinition T(string name) => new("dbo", name, [], null, []);
}
