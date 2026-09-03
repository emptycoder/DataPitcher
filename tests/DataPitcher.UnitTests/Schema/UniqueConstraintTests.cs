using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class UniqueConstraintTests
{
    [Fact]
    public void UniqueConstraint_WhenColumnListIsEmpty_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new UniqueConstraint("UQ_Empty", Array.Empty<string>()));
    }

    [Fact]
    public void UniqueConstraint_WhenSourceListMutatedAfterConstruction_IsUnaffected()
    {
        var source = new List<string> { "Id" };
        var constraint = new UniqueConstraint("UQ_Id", source);
        source.Add("Extra");
        Assert.Equal(["Id"], constraint.Columns);
    }
}
