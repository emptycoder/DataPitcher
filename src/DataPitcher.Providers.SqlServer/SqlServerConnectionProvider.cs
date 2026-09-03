using DataPitcher.Core.Connections;

namespace DataPitcher.Providers.SqlServer;

public sealed class SqlServerConnectionProvider : IConnectionProvider
{
    public string ProviderId => "sqlserver";
    public ICapabilityDetector CapabilityDetector { get; } = new SqlServerConnectionProbe();
    public ISchemaIntrospector SchemaIntrospector { get; } = new SqlServerSchemaIntrospector();
}
