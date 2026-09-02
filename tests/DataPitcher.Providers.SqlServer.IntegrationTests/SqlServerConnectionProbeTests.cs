using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerConnectionProbeTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task ProbeAsync_WhenSourceCanReadAndUseStaging_ReportsCapabilitiesAndRemovesProbeObject()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false);

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.ResumableStaged), CancellationToken.None);

        Assert.Contains(ConnectionCapability.CanConnect, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadSchema, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadBusinessRows, evidence.Available);
        Assert.Contains(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.Contains(ConnectionCapability.CanDropSourceStaging, evidence.Available);
        Assert.Equal(0, await scope.StagingObjectCountAsync());
        Assert.Null(evidence.CleanupFailureCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenTargetCanWriteAndUseStaging_ReportsTargetCapabilities()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Target, false);

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.ResumableStaged), CancellationToken.None);

        Assert.Contains(ConnectionCapability.CanBulkInsert, evidence.Available);
        Assert.Contains(ConnectionCapability.CanPreserveIdentity, evidence.Available);
        Assert.Contains(ConnectionCapability.CanCreateTargetStaging, evidence.Available);
        Assert.Contains(ConnectionCapability.CanDropTargetStaging, evidence.Available);
        Assert.NotEqual("", evidence.DatabaseIdentity);
        Assert.NotEqual("", evidence.ProviderVersion);
        Assert.Null(evidence.CleanupFailureCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCleanupFails_ReportsAnExplicitSafeFailure()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, true);

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.ResumableStaged), CancellationToken.None);

        Assert.NotNull(evidence.CleanupFailureCode);
        Assert.Equal("staging_cleanup_failed", evidence.CleanupFailureCode);
        Assert.DoesNotContain("Password=", evidence.CleanupFailureCode!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_WhenModeDoesNotUseDurableStaging_DoesNotCreateAStagingObject()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false);

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.DirectFast), CancellationToken.None);

        Assert.DoesNotContain(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.Equal(0, await scope.StagingObjectCountAsync());
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCreatePermissionIsMissing_DoesNotClaimStagingCapabilities()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false, false);

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.ResumableStaged), CancellationToken.None);

        Assert.DoesNotContain(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.DoesNotContain(ConnectionCapability.CanDropSourceStaging, evidence.Available);
        Assert.Null(evidence.CleanupFailureCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCreateFails_DoesNotClaimAConnectionCanBeProbed()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false, true, true);

        await Assert.ThrowsAsync<SqlException>(() => new SqlServerConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.ResumableStaged), CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_WhenSnapshotIsolationIsEnabled_ReportsTheCapability()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false);
        await scope.EnableSnapshotIsolationAsync();

        var evidence = await new SqlServerConnectionProbe().ProbeAsync(scope.Request(TransferMode.DirectFast), CancellationToken.None);

        Assert.Contains(ConnectionCapability.CanUseSnapshotIsolation, evidence.Available);
    }

    [Fact]
    public async Task ReadAsync_ConvertsTheExistingCatalogReaderResult()
    {
        await using var scope = await SqlServerProbeScope.CreateAsync(fixture, ConnectionRole.Source, false);

        var snapshot = await new SqlServerSchemaIntrospector().ReadAsync(scope.Profile, scope.ConnectionString, CancellationToken.None);

        Assert.Contains(snapshot.Tables, table => string.Equals(table.Name, "customers", StringComparison.Ordinal));
        Assert.Contains(snapshot.ForeignKeys, foreignKey => string.Equals(foreignKey.Name, "FK_composite_child_parent", StringComparison.Ordinal));
    }
}

internal sealed class SqlServerProbeScope : IAsyncDisposable
{
    private readonly SqlServerClosureScope _scope;
    private readonly string _login;
    private readonly string _stagingSchema;
    private readonly string? _blocker;

    private SqlServerProbeScope(SqlServerClosureScope scope, string login, string stagingSchema, string connectionString,
        ConnectionProfile profile, string? blocker)
    {
        _scope = scope;
        _login = login;
        _stagingSchema = stagingSchema;
        ConnectionString = connectionString;
        Profile = profile;
        _blocker = blocker;
    }

    public string ConnectionString { get; }
    public ConnectionProfile Profile { get; }

    public static async Task<SqlServerProbeScope> CreateAsync(SqlServerClosureFixture fixture, ConnectionRole role, bool denyDrop, bool grantStaging = true, bool denyCreate = false)
    {
        var scope = await fixture.CreateScopeAsync();
        var name = Guid.NewGuid().ToString("N");
        var login = "dp_probe_" + name;
        var stagingSchema = "dp_staging_" + name;
        const string password = "DataPitcherProbe!2026";
        await using var server = new SqlConnection(scope.SourceAdminConnectionString);
        await server.OpenAsync();
        await ExecuteAsync(server, $"CREATE LOGIN {Quote(login)} WITH PASSWORD = '{password}';");
        await using var database = new SqlConnection(scope.SourceConnectionString);
        await database.OpenAsync();
        await ExecuteAsync(database, $"CREATE USER {Quote(login)} FOR LOGIN {Quote(login)};");
        await ExecuteAsync(database, $"GRANT SELECT ON SCHEMA::[dbo] TO {Quote(login)};");
        await ExecuteAsync(database, "GRANT CREATE TABLE TO " + Quote(login) + ";");
        await ExecuteAsync(database, $"CREATE SCHEMA {Quote(stagingSchema)} AUTHORIZATION dbo;");
        if (grantStaging)
            await ExecuteAsync(database, $"GRANT ALTER ON SCHEMA::{Quote(stagingSchema)} TO {Quote(login)};");
        if (role is ConnectionRole.Target)
        {
            await ExecuteAsync(database, $"GRANT INSERT ON SCHEMA::[dbo] TO {Quote(login)};");
            await ExecuteAsync(database, $"GRANT ALTER ON SCHEMA::[dbo] TO {Quote(login)};");
        }

        string? blocker = null;
        if (denyDrop || denyCreate)
        {
            blocker = "dp_probe_drop_" + name;
            await ExecuteAsync(database,
                $"CREATE TRIGGER {Quote(blocker)} ON DATABASE FOR {(denyDrop ? "DROP_TABLE" : "CREATE_TABLE")} AS BEGIN IF ORIGINAL_LOGIN() = N'{login}' BEGIN ROLLBACK TRANSACTION; THROW 50000, 'staging operation denied', 1; END END;");
        }

        var connectionString = new SqlConnectionStringBuilder(scope.SourceConnectionString)
        {
            UserID = login,
            Password = password,
            Pooling = false,
        }.ConnectionString;
        var profile = new ConnectionProfile(Guid.NewGuid(), role.ToString(), "sqlserver",
            new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_PROBE"), "dbo", stagingSchema, 1);
        return new SqlServerProbeScope(scope, login, stagingSchema, connectionString, profile, blocker);
    }

    public ConnectionProbeRequest Request(TransferMode mode) => new(Profile,
        string.Equals(Profile.DisplayName, ConnectionRole.Source.ToString(), StringComparison.Ordinal) ? ConnectionRole.Source : ConnectionRole.Target,
        mode, ConnectionString);

    public async Task<int> StagingObjectCountAsync()
    {
        await using var connection = new SqlConnection(_scope.SourceConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema", connection);
        command.Parameters.AddWithValue("@schema", _stagingSchema);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task EnableSnapshotIsolationAsync()
    {
        await using var connection = new SqlConnection(_scope.SourceAdminConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE {Quote(_scope.Database)} SET ALLOW_SNAPSHOT_ISOLATION ON;");
    }

    public async ValueTask DisposeAsync()
    {
        await using var database = new SqlConnection(_scope.SourceConnectionString);
        await database.OpenAsync();
        if (_blocker is not null)
            await ExecuteAsync(database, $"DROP TRIGGER {Quote(_blocker)} ON DATABASE;");
        await ExecuteAsync(database,
            $"DECLARE @sql nvarchar(max) = N''; SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';' FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=N'{_stagingSchema}'; EXEC sp_executesql @sql; DROP SCHEMA {Quote(_stagingSchema)};");
        await _scope.DisposeAsync();
        await using var server = new SqlConnection(_scope.SourceAdminConnectionString);
        await server.OpenAsync();
        await ExecuteAsync(server, $"DROP LOGIN {Quote(_login)};");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
