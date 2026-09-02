using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlClosureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _source = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly PostgreSqlContainer _target = new PostgreSqlBuilder("postgres:17-alpine").Build();

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

    public async Task<PostgreSqlClosureScope> CreateScopeAsync(PostgreSqlCommandRecorder? targetRecorder = null, PostgreSqlCommandRecorder? sourceRecorder = null)
    {
        var schema = "dp_" + Guid.NewGuid().ToString("N");
        var source = CreateSourceDataSource(schema, sourceRecorder);
        var target = CreateTargetDataSource(schema, targetRecorder);
        await PostgreSqlClosureScope.CreateAsync(source, schema, false);
        await PostgreSqlClosureScope.CreateAsync(target, schema, true);
        return new PostgreSqlClosureScope(schema, source, target);
    }

    private NpgsqlDataSource CreateTargetDataSource(string schema, PostgreSqlCommandRecorder? recorder)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_target.GetConnectionString()) { SearchPath = schema }.ConnectionString;
        if (recorder is null)
            return NpgsqlDataSource.Create(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseLoggerFactory(recorder);
        return builder.Build();
    }

    private NpgsqlDataSource CreateSourceDataSource(string schema, PostgreSqlCommandRecorder? recorder)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_source.GetConnectionString()) { SearchPath = schema }.ConnectionString;
        if (recorder is null)
            return NpgsqlDataSource.Create(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseLoggerFactory(recorder);
        return builder.Build();
    }
}

public sealed class PostgreSqlClosureScope(string schema, NpgsqlDataSource source, NpgsqlDataSource target) : IAsyncDisposable
{
    public string Schema { get; } = schema;
    public NpgsqlDataSource Source { get; } = source;
    public NpgsqlDataSource Target { get; } = target;

    public static async Task CreateAsync(NpgsqlDataSource dataSource, string schema, bool target)
    {
        await ExecuteOnAsync(dataSource, "CREATE SCHEMA " + Quote(schema));
        foreach (var sql in SchemaSql(target)) await ExecuteOnAsync(dataSource, sql);
    }

    public Task ExecuteAsync(string sql) => ExecuteOnAsync(Source, sql);
    public Task ExecuteTargetAsync(string sql) => ExecuteOnAsync(Target, sql);

    public Task<T> ScalarAsync<T>(string sql) => ScalarOnAsync<T>(Source, sql);
    public Task<T> ScalarTargetAsync<T>(string sql) => ScalarOnAsync<T>(Target, sql);

    public async ValueTask DisposeAsync()
    {
        await ExecuteOnAsync(Source, "DROP SCHEMA IF EXISTS " + Quote(Schema) + " CASCADE");
        await ExecuteOnAsync(Target, "DROP SCHEMA IF EXISTS " + Quote(Schema) + " CASCADE");
        await Source.DisposeAsync();
        await Target.DisposeAsync();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecuteOnAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarOnAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static IEnumerable<string> SchemaSql(bool target) =>
    [
        "CREATE TABLE customers (customer_id integer PRIMARY KEY, external_code text NOT NULL UNIQUE)",
        "CREATE TABLE orders (order_id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES customers(customer_id))",
        "CREATE TABLE order_lines (line_id integer PRIMARY KEY, order_id integer NOT NULL REFERENCES orders(order_id))",
        "CREATE TABLE declared_key (physical_first integer NOT NULL, physical_second integer NOT NULL, CONSTRAINT pk_declared_key PRIMARY KEY (physical_second, physical_first))",
        "CREATE TABLE composite_parent (left_value integer NOT NULL, right_value integer NOT NULL, PRIMARY KEY (left_value, right_value))",
        "CREATE TABLE composite_child (id integer PRIMARY KEY, child_left integer NOT NULL, child_right integer NOT NULL, CONSTRAINT fk_composite_child_parent FOREIGN KEY (child_right, child_left) REFERENCES composite_parent(left_value, right_value))",
        "CREATE TABLE optional_orders (id integer PRIMARY KEY, customer_id integer NULL REFERENCES customers(customer_id))",
        "CREATE TABLE external_parents (id integer PRIMARY KEY, code text NOT NULL UNIQUE)",
        "CREATE TABLE external_children (id integer PRIMARY KEY, code text NOT NULL REFERENCES external_parents(code))",
        "CREATE TABLE employees (id integer PRIMARY KEY, manager_id integer NULL REFERENCES employees(id))",
        "CREATE TABLE cycle_a (id integer PRIMARY KEY, b_id integer NULL)",
        "CREATE TABLE cycle_b (id integer PRIMARY KEY, a_id integer NULL REFERENCES cycle_a(id))",
        "ALTER TABLE cycle_a ADD CONSTRAINT fk_cycle_a_b FOREIGN KEY (b_id) REFERENCES cycle_b(id)",
        "CREATE TABLE unique_only (code text NOT NULL UNIQUE)",
        "CREATE TABLE no_stable_key (value text NULL)",
        "CREATE TABLE untrusted_grandparents (id integer PRIMARY KEY)",
        "CREATE TABLE untrusted_parents (id integer PRIMARY KEY, grandparent_id integer NOT NULL)",
        target
            ? "INSERT INTO untrusted_parents VALUES (2,3); ALTER TABLE untrusted_parents ADD CONSTRAINT \"Target_FK_P_G\" FOREIGN KEY (grandparent_id) REFERENCES untrusted_grandparents(id) NOT VALID"
            : "ALTER TABLE untrusted_parents ADD CONSTRAINT \"FK_P_G\" FOREIGN KEY (grandparent_id) REFERENCES untrusted_grandparents(id)"
    ];
}
