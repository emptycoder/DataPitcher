using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class StableKeySelectorTests
{
    [Fact] public void StableKeySelector_WhenTableHasPrimaryKey_UsesPrimaryKey()
    {
        var primary = new UniqueConstraint("PK_Orders", ["Id"]);
        Assert.Equal(primary, StableKeySelector.Select(new TableDefinition("dbo", "Orders", [], primary, []), null).Constraint);
    }
    [Fact] public void StableKeySelector_WhenNoPrimaryKeyAndNoSelectedUnique_ReportsBlocked()
    {
        var result = StableKeySelector.Select(new TableDefinition("dbo", "Logs", [], null, [new("UQ_Code", ["Code"])]), null);
        Assert.False(result.HasStableKey);
    }
    [Fact] public void StableKeySelector_WhenUniqueConstraintExplicitlySelected_UsesIt()
    {
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        Assert.Equal(unique, StableKeySelector.Select(new TableDefinition("dbo", "Codes", [], null, [unique]), "UQ_Code").Constraint);
    }
    [Fact] public void TableDefinition_WhenRehydratedWithTheSameSchemaAndName_IsEqual()
    {
        var original = new TableDefinition("dbo", "Orders", [new("Id", typeof(int), false)], null, []);
        var rehydrated = new TableDefinition("dbo", "Orders", [new("Id", typeof(int), false)], null, []);
        Assert.Equal(original, rehydrated);
        Assert.Single(new HashSet<TableDefinition> { original, rehydrated });
    }
}
