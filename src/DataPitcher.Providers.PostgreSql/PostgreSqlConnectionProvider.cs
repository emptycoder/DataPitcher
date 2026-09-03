using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlConnectionProvider : IConnectionProvider
{
    public string ProviderId => "postgresql";
    public ICapabilityDetector CapabilityDetector { get; } = new PostgreSqlConnectionProbe();
    public ISchemaIntrospector SchemaIntrospector { get; } = new PostgreSqlSchemaIntrospector();
}
