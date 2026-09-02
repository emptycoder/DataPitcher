using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[CollectionDefinition("SqlServer closure", DisableParallelization = true)]
public sealed class SqlServerClosureCollection : ICollectionFixture<SqlServerClosureFixture> { }

public sealed class SqlServerClosureFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";
    private readonly MsSqlContainer _source = new MsSqlBuilder(Image).WithPassword("DataPitcher!Sql2026").Build();
    private readonly MsSqlContainer _target = new MsSqlBuilder(Image).WithPassword("DataPitcher!Sql2026").Build();

    public async Task InitializeAsync()
    {
        await _source.StartAsync();
        await _target.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    public async Task<SqlServerClosureScope> CreateScopeAsync()
    {
        var database = "dp_" + Guid.NewGuid().ToString("N");
        await CreateDatabaseAsync(_source.GetConnectionString(), database);
        await CreateDatabaseAsync(_target.GetConnectionString(), database);
        var source = WithDatabase(_source.GetConnectionString(), database);
        var target = WithDatabase(_target.GetConnectionString(), database);
        await SqlServerClosureScope.CreateAsync(source, false);
        await SqlServerClosureScope.CreateAsync(target, true);
        return new(database, source, target, _source.GetConnectionString(), _target.GetConnectionString());
    }

    private static string WithDatabase(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;

    private static async Task CreateDatabaseAsync(string connectionString, string database)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE DATABASE " + Quote(database) + ";";
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

public sealed class SqlServerClosureScope(string database, string source, string target, string sourceAdmin, string targetAdmin) : IAsyncDisposable
{
    public string Database { get; } = database;
    public string SourceConnectionString { get; } = source;
    public string TargetConnectionString { get; } = target;
    public string SourceAdminConnectionString { get; } = sourceAdmin;
    public string TargetAdminConnectionString { get; } = targetAdmin;

    public static async Task CreateAsync(string connectionString, bool target)
    {
        foreach (var sql in SchemaSql(target))
            await ExecuteOnAsync(connectionString, sql);
    }

    public Task ExecuteAsync(string sql) => ExecuteOnAsync(SourceConnectionString, sql);
    public Task ExecuteTargetAsync(string sql) => ExecuteOnAsync(TargetConnectionString, sql);
    public Task<T> ScalarAsync<T>(string sql) => ScalarOnAsync<T>(SourceConnectionString, sql);
    public Task<T> ScalarTargetAsync<T>(string sql) => ScalarOnAsync<T>(TargetConnectionString, sql);

    public async ValueTask DisposeAsync()
    {
        await DropAsync(SourceAdminConnectionString);
        await DropAsync(TargetAdminConnectionString);
    }

    private async Task DropAsync(string admin)
    {
        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}];";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarOnAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result, typeof(T))!;
    }

    private static IEnumerable<string> SchemaSql(bool target) =>
    [
        "CREATE TABLE dbo.customers (customer_id int NOT NULL PRIMARY KEY, external_code nvarchar(64) NOT NULL UNIQUE)",
        "CREATE TABLE dbo.orders (order_id int NOT NULL PRIMARY KEY, customer_id int NOT NULL REFERENCES dbo.customers(customer_id))",
        "CREATE TABLE dbo.order_lines (line_id int NOT NULL PRIMARY KEY, order_id int NOT NULL REFERENCES dbo.orders(order_id))",
        "CREATE TABLE dbo.declared_key (physical_first int NOT NULL, physical_second int NOT NULL, CONSTRAINT PK_declared_key PRIMARY KEY (physical_second, physical_first))",
        "CREATE TABLE dbo.composite_parent (left_value int NOT NULL, right_value int NOT NULL, PRIMARY KEY (left_value, right_value))",
        "CREATE TABLE dbo.composite_child (id int NOT NULL PRIMARY KEY, child_left int NOT NULL, child_right int NOT NULL, CONSTRAINT FK_composite_child_parent FOREIGN KEY (child_right, child_left) REFERENCES dbo.composite_parent(left_value, right_value))",
        "CREATE TABLE dbo.optional_orders (id int NOT NULL PRIMARY KEY, customer_id int NULL REFERENCES dbo.customers(customer_id))",
        "CREATE TABLE dbo.external_parents (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL UNIQUE)",
        "CREATE TABLE dbo.external_children (id int NOT NULL PRIMARY KEY, code nvarchar(64) NOT NULL REFERENCES dbo.external_parents(code))",
        "CREATE TABLE dbo.employees (id int NOT NULL PRIMARY KEY, manager_id int NULL REFERENCES dbo.employees(id))",
        "CREATE TABLE dbo.cycle_a (id int NOT NULL PRIMARY KEY, b_id int NULL)",
        "CREATE TABLE dbo.cycle_b (id int NOT NULL PRIMARY KEY, a_id int NULL REFERENCES dbo.cycle_a(id))",
        "ALTER TABLE dbo.cycle_a ADD CONSTRAINT FK_cycle_a_b FOREIGN KEY (b_id) REFERENCES dbo.cycle_b(id)",
        "CREATE TABLE dbo.unique_only (code nvarchar(64) NOT NULL UNIQUE)",
        "CREATE TABLE dbo.no_stable_key (value nvarchar(64) NULL)",
        "CREATE TABLE dbo.untrusted_grandparents (id int NOT NULL PRIMARY KEY)",
        "CREATE TABLE dbo.untrusted_parents (id int NOT NULL PRIMARY KEY, grandparent_id int NOT NULL)",
        target
            ? "INSERT dbo.untrusted_parents VALUES (2,3); ALTER TABLE dbo.untrusted_parents WITH NOCHECK ADD CONSTRAINT Target_FK_P_G FOREIGN KEY (grandparent_id) REFERENCES dbo.untrusted_grandparents(id)"
            : "ALTER TABLE dbo.untrusted_parents WITH CHECK ADD CONSTRAINT FK_P_G FOREIGN KEY (grandparent_id) REFERENCES dbo.untrusted_grandparents(id)"
    ];
}
