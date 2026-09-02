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
    private static TableDefinition Table(string name) => new("dbo", name, [], null, []);
}
