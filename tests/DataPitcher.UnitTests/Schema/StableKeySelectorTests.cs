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
        Assert.Equal(unique, StableKeySelector.Select(new TableDefinition("dbo", "Codes", [new ColumnDefinition("Code", typeof(string), false)], null, [unique]), "UQ_Code").Constraint);
    }
    [Fact] public void StableKeySelector_WhenSelectedConstraintNamesUnknownColumn_ReportsBlocked()
    {
        var unique = new UniqueConstraint("UQ_Ghost", ["Ghost"]);
        var table = new TableDefinition("dbo", "Codes", [], null, [unique]);
        var result = StableKeySelector.Select(table, "UQ_Ghost");
        Assert.False(result.HasStableKey);
    }
    [Fact] public void TableDefinition_WhenRehydratedWithTheSameSchemaAndName_IsEqual()
    {
        var original = new TableDefinition("dbo", "Orders", [new("Id", typeof(int), false)], null, []);
        var rehydrated = new TableDefinition("dbo", "Orders", [new("Id", typeof(int), false)], null, []);
        Assert.Equal(original, rehydrated);
        Assert.Single(new HashSet<TableDefinition> { original, rehydrated });
    }
    [Fact] public void StableKeySelector_WhenSelectedUniqueConstraintHasNullableColumn_ReportsBlocked()
    {
        var table = new TableDefinition(
            "dbo", "Widgets",
            [new ColumnDefinition("Code", typeof(string), true)],
            null,
            [new UniqueConstraint("UQ_Code", ["Code"])]);
        var result = StableKeySelector.Select(table, "UQ_Code");
        Assert.False(result.HasStableKey);
    }
    [Fact] public void StableKeySelector_WhenSelectedUniqueConstraintColumnsAreAllNonNull_UsesIt()
    {
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        var table = new TableDefinition(
            "dbo", "Widgets",
            [new ColumnDefinition("Code", typeof(string), false)],
            null,
            [unique]);
        var result = StableKeySelector.Select(table, "UQ_Code");
        Assert.True(result.HasStableKey);
        Assert.Equal(unique, result.Constraint);
    }
}
