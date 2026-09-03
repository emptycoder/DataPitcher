using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerConnectionProvider : IConnectionProvider
{
    static SqlServerConnectionProvider() => SqlServerEntraAuthentication.EnsureRegistered();

    public string ProviderId => "sqlserver";
    public ICapabilityDetector CapabilityDetector { get; } = new SqlServerConnectionProbe();
    public ISchemaIntrospector SchemaIntrospector { get; } = new SqlServerSchemaIntrospector();
}
