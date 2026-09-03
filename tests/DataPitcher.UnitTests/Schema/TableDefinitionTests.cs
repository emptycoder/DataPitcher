using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class TableDefinitionTests
{
    [Fact]
    public void TableDefinition_WhenSourceListMutatedAfterConstruction_IsUnaffected()
    {
        var sourceColumns = new List<ColumnDefinition> { new("Id", typeof(int), false) };
        var table = new TableDefinition("dbo", "Orders", sourceColumns, null, []);
        sourceColumns.Add(new ColumnDefinition("Extra", typeof(string), true));
        Assert.Single(table.Columns);
    }

    [Fact]
    public void TableDefinition_WhenSchemaAndNameMatch_AreEqualAndProduceEqualHashCodesRegardlessOfColumns()
    {
        var left = new TableDefinition("dbo", "Orders", [new ColumnDefinition("Id", typeof(int), false)], null, []);
        var right = new TableDefinition("dbo", "Orders", [], new UniqueConstraint("PK", ["Id"]), []);
        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
        Assert.True(right.Equals(left));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void TableDefinition_WhenSchemaDiffers_AreNotEqual() =>
        Assert.NotEqual(
            new TableDefinition("dbo", "Orders", [], null, []),
            new TableDefinition("sales", "Orders", [], null, [])
        );

    [Fact]
    public void TableDefinition_WhenNameDiffers_AreNotEqual() =>
        Assert.NotEqual(
            new TableDefinition("dbo", "Orders", [], null, []),
            new TableDefinition("dbo", "Customers", [], null, [])
        );
}
