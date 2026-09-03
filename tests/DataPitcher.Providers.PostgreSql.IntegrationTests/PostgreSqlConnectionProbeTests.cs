using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Providers.PostgreSql;
using Npgsql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlConnectionProbeTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlConnectionProbeTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProbeAsync_WhenSourceCanReadAndUseStaging_ReportsCapabilitiesAndRemovesProbeObject()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, false);

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.ResumableStaged),
            CancellationToken.None
        );

        Assert.Contains(ConnectionCapability.CanConnect, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadSchema, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadBusinessRows, evidence.Available);
        Assert.Contains(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.Contains(ConnectionCapability.CanDropSourceStaging, evidence.Available);
        Assert.Contains(ConnectionCapability.SupportsDurableResume, evidence.Available);
        Assert.NotEqual("", evidence.DatabaseIdentity);
        Assert.NotEqual("", evidence.ProviderVersion);
        Assert.Null(evidence.CleanupFailureCode);
        Assert.Equal(0, await scope.StagingObjectCountAsync());
    }

    [Fact]
    public async Task ProbeAsync_WhenTargetCanWriteAndUseStaging_ReportsTargetCapabilities()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Target, false);

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.ResumableStaged),
            CancellationToken.None
        );

        Assert.Contains(ConnectionCapability.CanBulkInsert, evidence.Available);
        Assert.Contains(ConnectionCapability.CanPreserveIdentity, evidence.Available);
        Assert.Contains(ConnectionCapability.CanCreateTargetStaging, evidence.Available);
        Assert.Contains(ConnectionCapability.CanDropTargetStaging, evidence.Available);
        Assert.Null(evidence.CleanupFailureCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCleanupFails_ReportsAnExplicitSafeFailure()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, true);

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.ResumableStaged),
            CancellationToken.None
        );

        Assert.NotNull(evidence.CleanupFailureCode);
        Assert.Equal("staging_cleanup_failed", evidence.CleanupFailureCode);
        Assert.DoesNotContain("Password=", evidence.CleanupFailureCode!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_WhenModeDoesNotUseDurableStaging_DoesNotCreateAStagingObject()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, false);

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.DirectFast),
            CancellationToken.None
        );

        Assert.DoesNotContain(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.Equal(0, await scope.StagingObjectCountAsync());
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCreatePermissionIsMissing_DoesNotClaimStagingCapabilities()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, false, false);

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.ResumableStaged),
            CancellationToken.None
        );

        Assert.DoesNotContain(ConnectionCapability.CanCreateSourceStaging, evidence.Available);
        Assert.DoesNotContain(ConnectionCapability.CanDropSourceStaging, evidence.Available);
        Assert.Null(evidence.CleanupFailureCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenStagingCreateFails_DoesNotClaimAConnectionCanBeProbed()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(
            _fixture,
            ConnectionRole.Source,
            false,
            true,
            true
        );

        await Assert.ThrowsAsync<PostgresException>(() =>
            new PostgreSqlConnectionProbe().ProbeAsync(
                scope.Request(TransferMode.ResumableStaged),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task ReadAsync_ConvertsTheExistingCatalogReaderResult()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, false);

        var snapshot = await new PostgreSqlSchemaIntrospector().ReadAsync(
            scope.Profile,
            scope.ConnectionString,
            CancellationToken.None
        );

        Assert.Contains(snapshot.Tables, table => string.Equals(table.Name, "customers", StringComparison.Ordinal));
        Assert.Contains(
            snapshot.ForeignKeys,
            foreignKey => string.Equals(foreignKey.Name, "orders_customer_id_fkey", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ProbeAsync_WhenBusinessSchemaDoesNotExist_StillReportsReadCapabilitiesForAnAdmin()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(_fixture, ConnectionRole.Source, false);
        var request = new ConnectionProbeRequest(
            scope.Profile with
            {
                BusinessSchema = "app",
            },
            ConnectionRole.Source,
            TransferMode.DirectFast,
            scope.AdminConnectionString
        );

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(request, CancellationToken.None);

        Assert.Contains(ConnectionCapability.CanReadSchema, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadBusinessRows, evidence.Available);
    }

    [Fact]
    public async Task ProbeAsync_WhenOnlyOneTableIsReadable_ReportsReadCapabilitiesWithoutSchemaWideGrants()
    {
        await using var scope = await PostgreSqlProbeScope.CreateAsync(
            _fixture,
            ConnectionRole.Source,
            false,
            grantStaging: false,
            singleTableOnly: true
        );

        var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(
            scope.Request(TransferMode.DirectFast),
            CancellationToken.None
        );

        Assert.Contains(ConnectionCapability.CanReadSchema, evidence.Available);
        Assert.Contains(ConnectionCapability.CanReadBusinessRows, evidence.Available);
        Assert.DoesNotContain(ConnectionCapability.CanBulkInsert, evidence.Available);
    }
}

internal sealed class PostgreSqlProbeScope : IAsyncDisposable
{
    private readonly PostgreSqlClosureScope _scope;
    private readonly string _role;
    private readonly string _stagingSchema;
    private readonly string? _blocker;
    private readonly string? _blockerFunction;

    private PostgreSqlProbeScope(
        PostgreSqlClosureScope scope,
        string role,
        string stagingSchema,
        string connectionString,
        ConnectionProfile profile,
        string? blocker,
        string? blockerFunction
    )
    {
        _scope = scope;
        _role = role;
        _stagingSchema = stagingSchema;
        ConnectionString = connectionString;
        Profile = profile;
        _blocker = blocker;
        _blockerFunction = blockerFunction;
    }

    public string ConnectionString { get; }
    public ConnectionProfile Profile { get; }
    public string AdminConnectionString { get; private set; } = "";

    public static async Task<PostgreSqlProbeScope> CreateAsync(
        PostgreSqlClosureFixture fixture,
        ConnectionRole role,
        bool denyDrop,
        bool grantStaging = true,
        bool denyCreate = false,
        bool singleTableOnly = false
    )
    {
        var scope = await fixture.CreateScopeAsync();
        var name = Guid.NewGuid().ToString("N");
        var login = "dp_probe_" + name;
        var stagingSchema = "dp_staging_" + name;
        var password = "DataPitcherProbe!2026";
        await using var admin = await scope.Source.OpenConnectionAsync();
        await ExecuteAsync(admin, $"CREATE ROLE {Quote(login)} LOGIN PASSWORD '{password}';");
        await ExecuteAsync(admin, $"GRANT CONNECT ON DATABASE {Quote(admin.Database)} TO {Quote(login)};");
        await ExecuteAsync(admin, $"GRANT USAGE ON SCHEMA {Quote(scope.Schema)} TO {Quote(login)};");
        if (singleTableOnly)
            await ExecuteAsync(admin, $"GRANT SELECT ON {Quote(scope.Schema)}.customers TO {Quote(login)};");
        else
            await ExecuteAsync(admin, $"GRANT SELECT ON ALL TABLES IN SCHEMA {Quote(scope.Schema)} TO {Quote(login)};");
        await ExecuteAsync(admin, $"CREATE SCHEMA {Quote(stagingSchema)};");
        if (grantStaging)
            await ExecuteAsync(admin, $"GRANT USAGE, CREATE ON SCHEMA {Quote(stagingSchema)} TO {Quote(login)};");
        if (role is ConnectionRole.Target)
            await ExecuteAsync(admin, $"GRANT INSERT ON ALL TABLES IN SCHEMA {Quote(scope.Schema)} TO {Quote(login)};");

        string? blocker = null;
        string? blockerFunction = null;
        if (denyDrop || denyCreate)
        {
            blocker = "dp_probe_drop_" + name;
            blockerFunction = blocker + "_function";
            await ExecuteAsync(
                admin,
                $"CREATE FUNCTION {Quote(blockerFunction)}() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN IF current_user = '{login}' THEN RAISE EXCEPTION 'drop denied'; END IF; END; $$;"
            );
            await ExecuteAsync(
                admin,
                $"CREATE EVENT TRIGGER {Quote(blocker)} ON ddl_command_start WHEN TAG IN ({(denyDrop ? "'DROP TABLE'" : "'CREATE TABLE'")}) EXECUTE FUNCTION {Quote(blockerFunction)}();"
            );
        }

        var connectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = false,
        }.ConnectionString;
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            role.ToString(),
            "postgresql",
            new SecretReference(SecretReferenceKind.EnvironmentVariable, "DP_PROBE"),
            scope.Schema,
            stagingSchema,
            1
        );
        return new PostgreSqlProbeScope(
            scope,
            login,
            stagingSchema,
            connectionString,
            profile,
            blocker,
            blockerFunction
        )
        {
            AdminConnectionString = scope.SourceConnectionString,
        };
    }

    public ConnectionProbeRequest Request(TransferMode mode) =>
        new(
            Profile,
            string.Equals(Profile.DisplayName, ConnectionRole.Source.ToString(), StringComparison.Ordinal)
                ? ConnectionRole.Source
                : ConnectionRole.Target,
            mode,
            ConnectionString
        );

    public async Task<int> StagingObjectCountAsync()
    {
        await using var connection = await _scope.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema",
            connection
        );
        command.Parameters.AddWithValue("schema", _stagingSchema);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = await _scope.Source.OpenConnectionAsync();
        if (_blocker is not null)
        {
            await ExecuteAsync(admin, $"DROP EVENT TRIGGER {Quote(_blocker)};");
            await ExecuteAsync(admin, $"DROP FUNCTION {Quote(_blockerFunction!)}();");
        }
        await ExecuteAsync(admin, $"DROP OWNED BY {Quote(_role)};");
        await ExecuteAsync(admin, $"DROP SCHEMA {Quote(_stagingSchema)} CASCADE;");
        await ExecuteAsync(admin, $"DROP ROLE {Quote(_role)};");
        await _scope.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
