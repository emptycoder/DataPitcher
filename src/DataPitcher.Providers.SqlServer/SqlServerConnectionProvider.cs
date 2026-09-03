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

    public async Task<long> CountSelectionRootsAsync(
        ConnectionProfile profile,
        string connectionString,
        SelectionRootQuery query,
        CancellationToken cancellationToken
    )
    {
        var catalog = await new SqlServerCatalogReader(connectionString).ReadAsync(query.Schema, cancellationToken);
        var root = catalog.Table(query.Schema, query.Table).Definition;
        var sql = new GeneratedSelectionSql(
            query.RawSql,
            root,
            new UniqueConstraint(query.StableKeyName, query.StableKeyColumns),
            query.Parameters,
            true
        );
        var executor = new SqlServerSelectionExecutor(connectionString, catalog);
        await executor.ValidateAsync(sql, cancellationToken);
        return await executor.CountAsync(sql, cancellationToken);
    }
}
