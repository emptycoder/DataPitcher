using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class ForeignKeyDefinitionTests
{
    [Fact] public void ForeignKey_WhenChildAndParentColumnCountsDiffer_IsRejected()
    {
        var child = Table("Child"); var parent = Table("Parent");
        Assert.Throws<ArgumentException>(() => new ForeignKeyDefinition("FK", child, parent, ["A"], ["A", "B"], true, true));
    }
    [Fact] public void ForeignKeyDefinition_WhenColumnListsAreEmpty_IsRejected()
    {
        var child = Table("Child"); var parent = Table("Parent");
        Assert.Throws<ArgumentException>(() => new ForeignKeyDefinition("FK", child, parent, [], [], true, true));
    }
    [Fact] public void ForeignKeyDefinition_WhenSourceListMutatedAfterConstruction_IsUnaffected()
    {
        var child = Table("Child"); var parent = Table("Parent");
        var sourceChildColumns = new List<string> { "ParentId" };
        var fk = new ForeignKeyDefinition("FK", child, parent, sourceChildColumns, ["Id"], true, true);
        sourceChildColumns.Add("Extra");
        Assert.Single(fk.ChildColumns);
    }
    private static TableDefinition Table(string name) => new("dbo", name, [], null, []);
}
