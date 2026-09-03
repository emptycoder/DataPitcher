using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.ControlStore;

public sealed class ControlDatabase(string connectionString)
{
    /// <summary>Native SQLite access. Stores are migrating to this; <see cref="Open"/> remains during the migration.</summary>
    public ControlConnection OpenNative() => new(connectionString);

    public DataConnection Open()
    {
        var connection = new DataConnection(
            new DataOptions().UseConnectionString(ProviderName.SQLiteMS, connectionString)
        );
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }
}
