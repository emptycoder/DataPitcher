using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class ForeignKeyDefinitionTests
{
    [Fact]
    public void ForeignKey_WhenChildAndParentColumnCountsDiffer_IsRejected()
    {
        var child = Table("Child");
        var parent = Table("Parent");
        Assert.Throws<ArgumentException>(() =>
            new ForeignKeyDefinition("FK", child, parent, ["A"], ["A", "B"], true, true)
        );
    }

    [Fact]
    public void ForeignKeyDefinition_WhenColumnListsAreEmpty_IsRejected()
    {
        var child = Table("Child");
        var parent = Table("Parent");
        Assert.Throws<ArgumentException>(() => new ForeignKeyDefinition("FK", child, parent, [], [], true, true));
    }

    [Fact]
    public void ForeignKeyDefinition_WhenSourceListMutatedAfterConstruction_IsUnaffected()
    {
        var child = Table("Child");
        var parent = Table("Parent");
        var sourceChildColumns = new List<string> { "ParentId" };
        var fk = new ForeignKeyDefinition("FK", child, parent, sourceChildColumns, ["Id"], true, true);
        sourceChildColumns.Add("Extra");
        Assert.Single(fk.ChildColumns);
    }

    [Fact]
    public void ForeignKey_WhenAllFieldsMatch_AreEqualAndProduceEqualHashCodes()
    {
        var left = Fk();
        var right = Fk();
        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
        Assert.True(right.Equals(left));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void ForeignKey_WhenNameDiffers_AreNotEqual() => Assert.NotEqual(Fk(name: "FK1"), Fk(name: "FK2"));

    [Fact]
    public void ForeignKey_WhenChildTableDiffers_AreNotEqual() =>
        Assert.NotEqual(Fk(child: Table("Child1")), Fk(child: Table("Child2")));

    [Fact]
    public void ForeignKey_WhenParentTableDiffers_AreNotEqual() =>
        Assert.NotEqual(Fk(parent: Table("Parent1")), Fk(parent: Table("Parent2")));

    [Fact]
    public void ForeignKey_WhenChildColumnsDiffer_AreNotEqual() =>
        Assert.NotEqual(Fk(childColumns: ["A"]), Fk(childColumns: ["B"]));

    [Fact]
    public void ForeignKey_WhenParentColumnsDiffer_AreNotEqual() =>
        Assert.NotEqual(Fk(parentColumns: ["A"]), Fk(parentColumns: ["B"]));

    [Fact]
    public void ForeignKey_WhenIsEnforcedDiffers_AreNotEqual() =>
        Assert.NotEqual(Fk(isEnforced: true), Fk(isEnforced: false));

    [Fact]
    public void ForeignKey_WhenIsTrustedDiffers_AreNotEqual() =>
        Assert.NotEqual(Fk(isTrusted: true), Fk(isTrusted: false));

    [Fact]
    public void ForeignKey_Equals_WhenComparedToNullOrUnrelatedType_ReturnsFalse()
    {
        var fk = Fk();
        Assert.False(fk.Equals(null));
        Assert.False(fk.Equals("not a foreign key"));
    }

    private static TableDefinition Table(string name) => new("dbo", name, [], null, []);

    private static ForeignKeyDefinition Fk(
        string name = "FK",
        TableDefinition? child = null,
        TableDefinition? parent = null,
        string[]? childColumns = null,
        string[]? parentColumns = null,
        bool isEnforced = true,
        bool isTrusted = true
    ) =>
        new(
            name,
            child ?? Table("Child"),
            parent ?? Table("Parent"),
            childColumns ?? ["ChildId"],
            parentColumns ?? ["ParentId"],
            isEnforced,
            isTrusted
        );
}
