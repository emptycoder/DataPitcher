using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Storage;

public sealed class ControlDatabase(string connectionString)
{
    public DataConnection Open()
    {
        var connection = new DataConnection(new DataOptions().UseConnectionString(ProviderName.SQLiteMS, connectionString));
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }
}
