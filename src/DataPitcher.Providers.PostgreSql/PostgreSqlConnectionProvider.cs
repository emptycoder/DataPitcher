using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Npgsql;

namespace DataPitcher.Providers.PostgreSql;

public sealed class PostgreSqlConnectionProvider : IConnectionProvider
{
    public string ProviderId => "postgresql";
    public ICapabilityDetector CapabilityDetector { get; } = new PostgreSqlConnectionProbe();
    public ISchemaIntrospector SchemaIntrospector { get; } = new PostgreSqlSchemaIntrospector();

    public async Task<long> CountSelectionRootsAsync(
        ConnectionProfile profile,
        string connectionString,
        SelectionRootQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var catalog = await new PostgreSqlCatalogReader(dataSource).ReadAsync(query.Schema, cancellationToken);
        var root = catalog.Table(query.Schema, query.Table).Definition;
        var sql = new GeneratedSelectionSql(
            query.RawSql,
            root,
            new UniqueConstraint(query.StableKeyName, query.StableKeyColumns),
            query.Parameters,
            true
        );
        var executor = new PostgreSqlSelectionExecutor(dataSource, catalog);
        await executor.ValidateAsync(sql, cancellationToken);
        return await executor.CountAsync(sql, cancellationToken);
    }
}
