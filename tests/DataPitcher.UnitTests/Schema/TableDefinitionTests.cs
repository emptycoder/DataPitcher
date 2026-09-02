using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Schema;

public sealed class TableDefinitionTests
{
    [Fact] public void TableDefinition_WhenSourceListMutatedAfterConstruction_IsUnaffected()
    {
        var sourceColumns = new List<ColumnDefinition> { new("Id", typeof(int), false) };
        var table = new TableDefinition("dbo", "Orders", sourceColumns, null, []);
        sourceColumns.Add(new ColumnDefinition("Extra", typeof(string), true));
        Assert.Single(table.Columns);
    }
}
