using DataPitcher.ControlStore;
using LinqToDB.Data;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class ControlDatabaseTests
{
    [Fact]
    public void ControlDatabase_WhenOpened_ExecutesSQLiteThroughLinqToDb()
    {
        using var connection = new ControlDatabase("Data Source=:memory:").Open();
        Assert.Single(connection.Query<int>("SELECT 1").ToList());
    }
}
