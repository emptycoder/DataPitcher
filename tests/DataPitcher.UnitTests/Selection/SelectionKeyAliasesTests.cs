using DataPitcher.Core.Selection;
using Xunit;

namespace DataPitcher.UnitTests.Selection;

public sealed class SelectionKeyAliasesTests
{
    [Theory]
    [InlineData("SELECT id AS [__datapitcher_key_0] FROM dbo.orders", true)]
    [InlineData("SELECT * FROM dbo.orders WHERE 1 = 1", false)]
    [InlineData("select id, customer_id from orders", false)]
    public void AreProjectedBy_DetectsQueriesThatSpellTheAliasesOut(string sql, bool expected) =>
        Assert.Equal(expected, SelectionKeyAliases.AreProjectedBy(sql));
}
