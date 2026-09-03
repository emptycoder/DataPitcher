using DataPitcher.Core.Connections;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlConnectionProvider : IConnectionProvider
{
    public string ProviderId => "postgresql";
    public ICapabilityDetector CapabilityDetector { get; } = new PostgreSqlConnectionProbe();
    public ISchemaIntrospector SchemaIntrospector { get; } = new PostgreSqlSchemaIntrospector();
}
