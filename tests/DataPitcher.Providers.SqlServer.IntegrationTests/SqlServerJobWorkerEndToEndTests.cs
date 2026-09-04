using System.Text.Json;
using DataPitcher.Api.Composition;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Plans;
using DataPitcher.Application.Schema;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerJobWorkerEndToEndTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Transfer_WhenPlanIsSealedAndJobStarts_CopiesReferentiallyRequiredRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY); CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.worker_parents(id)); INSERT dbo.worker_parents VALUES (2); INSERT dbo.worker_children VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY); CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.worker_parents(id));"
        );
        await EnableSnapshotIsolationAsync(scope.SourceAdminConnectionString, scope.Database);
        await EnableSnapshotIsolationAsync(scope.TargetAdminConnectionString, scope.Database);
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-job-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSecret = Path.Combine(directory, "source");
            var targetSecret = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(sourceSecret, scope.SourceConnectionString);
            await File.WriteAllTextAsync(targetSecret, scope.TargetConnectionString);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ControlDatabase:Path"] = Path.Combine(directory, "control.db"),
                        ["Secrets:Root"] = directory,
                        ["Worker:LeaseTtl"] = "00:00:30",
                        ["Worker:PollInterval"] = "00:00:00.050",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddDataPitcherComposition(configuration);
            using var provider = services.BuildServiceProvider();
            provider.ApplyControlDatabaseMigrations();
            var profiles = provider.GetRequiredService<ConnectionProfileStore>();
            var source = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "source",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    "dbo",
                    "dbo"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    "dbo",
                    "dbo"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.Equal(SchemaScanState.Completed, completedScan.State);
            var snapshotId =
                completedScan.SnapshotId
                ?? throw new InvalidOperationException("Source schema scan did not create a snapshot.");
            var selectionId = Guid.NewGuid();
            await provider
                .GetRequiredService<SelectionStore>()
                .SaveAsync(
                    selectionId,
                    "children",
                    JsonSerializer.Serialize(
                        new
                        {
                            Mode = "raw",
                            RawSql = "SELECT id AS [__datapitcher_key_0] FROM dbo.worker_children",
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    snapshotId,
                    "dbo",
                    "worker_children",
                    "PK_worker_children",
                    ["id"]
                );
            var application = provider.GetRequiredService<IDataPitcherApplication>();
            var planId = Guid.NewGuid();
            await application.SavePlanAsync(
                planId,
                new SavePlanRequest(
                    "worker plan",
                    null,
                    "\"0\"",
                    selectionId,
                    source.ConnectionId,
                    target.ConnectionId
                ),
                CancellationToken.None
            );
            await application.QueuePlanSealAsync(planId, CancellationToken.None);
            // The parent appears in the target after sealing (a re-run, or a concurrent writer): the transfer must
            // skip it and report it rather than fail on the primary key.
            await scope.ExecuteTargetAsync("INSERT dbo.worker_parents VALUES (2);");
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                var job = await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );

                var events = await provider
                    .GetRequiredService<IJobEventReader>()
                    .ReadAfterAsync(job.JobId, null, CancellationToken.None);
                Assert.True(
                    job.State is JobState.Succeeded,
                    "Expected job state Succeeded but was "
                        + job.State
                        + ". Events: "
                        + string.Join(", ", events.Events.Select(jobEvent => jobEvent.Payload.State))
                );
                Assert.Equal(
                    1,
                    await scope.ScalarTargetAsync<int>(
                        "SELECT COUNT(*) FROM dbo.worker_children WHERE id = 1 AND parent_id = 2"
                    )
                );
                Assert.Equal(
                    1,
                    await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_parents WHERE id = 2")
                );
                // The parent already existed in the target: it is skipped and reported, never a failure.
                var conflict = Assert.Single(events.Events, jobEvent => jobEvent.EventType == "conflict");
                Assert.Contains("dbo.worker_parents", conflict.Payload.Detail, StringComparison.Ordinal);
                Assert.Contains("1 row(s)", conflict.Payload.Detail, StringComparison.Ordinal);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Transfer_WhenATableReferencesItselfAcrossBatches_WritesAncestorsFirst()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_nodes (id int NOT NULL CONSTRAINT PK_worker_nodes PRIMARY KEY, parent_id int NULL CONSTRAINT FK_worker_nodes_parent FOREIGN KEY REFERENCES dbo.worker_nodes(id));";
        // A root that is its own parent with 2,000 children the 2,000-row batches only reach after it, plus a two-way
        // cycle (2002 <-> 2003) with rows below it that no levelling can reach.
        await scope.ExecuteAsync(
            ddl
                + " INSERT dbo.worker_nodes (id, parent_id) VALUES (2001, 2001); WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b) INSERT dbo.worker_nodes (id, parent_id) SELECT i, 2001 FROM n;"
                + " INSERT dbo.worker_nodes (id, parent_id) VALUES (2002, NULL), (2003, 2002); UPDATE dbo.worker_nodes SET parent_id = 2003 WHERE id = 2002;"
                + " INSERT dbo.worker_nodes (id, parent_id) VALUES (2004, 2003), (2005, 2004);"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_nodes", "PK_worker_nodes", "SELECT * FROM dbo.worker_nodes");

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2005, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_nodes"));
        Assert.Equal(
            0,
            await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_nodes WHERE parent_id IS NULL")
        );
        Assert.Equal(
            2000L * 2001 + 2001 + 2003 + 2002 + 2003 + 2004,
            await scope.ScalarTargetAsync<long>("SELECT SUM(CAST(parent_id AS bigint)) FROM dbo.worker_nodes")
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_worker_nodes_parent' AND is_disabled = 0 AND is_not_trusted = 0"
            )
        );
    }

    [Fact]
    public async Task Transfer_WhenATableReferencesItselfThroughAUniqueKeyThatIsNotTheStableKey_WritesAncestorsFirst()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_codes (id int NOT NULL CONSTRAINT PK_worker_codes PRIMARY KEY, code nvarchar(32) NOT NULL CONSTRAINT UQ_worker_codes UNIQUE, parent_code nvarchar(32) NULL CONSTRAINT FK_worker_codes_parent FOREIGN KEY REFERENCES dbo.worker_codes(code));";
        // The stable key is id, the parent link goes through code: every child has a lower id than its parent.
        await scope.ExecuteAsync(
            ddl
                + " INSERT dbo.worker_codes (id, code, parent_code) VALUES (2001, N'root', NULL); WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b) INSERT dbo.worker_codes (id, code, parent_code) SELECT i, CONCAT(N'c', i), N'root' FROM n;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_codes", "PK_worker_codes", "SELECT * FROM dbo.worker_codes");

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2001, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_codes"));
        Assert.Equal(
            2000,
            await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_codes WHERE parent_code = N'root'")
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_worker_codes_parent' AND is_disabled = 0 AND is_not_trusted = 0"
            )
        );
    }

    [Fact]
    public async Task Transfer_WhenTheTargetDeclaresTablesAndColumnsInAnotherCase_MapsThemAnyway()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY, code nvarchar(32) NOT NULL);"
                + " CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.worker_parents(id));"
                + " INSERT dbo.worker_parents VALUES (2, N'two'); INSERT dbo.worker_children VALUES (1, 2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.WORKER_PARENTS (ID int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY, CODE nvarchar(32) NOT NULL);"
                + " CREATE TABLE dbo.WORKER_CHILDREN (ID int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, PARENT_ID int NOT NULL CONSTRAINT FK_worker_children_parents FOREIGN KEY REFERENCES dbo.WORKER_PARENTS(ID));"
        );

        var job = await RunTransferAsync(
            scope,
            "worker_children",
            "PK_worker_children",
            "SELECT * FROM dbo.worker_children"
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal("two", await scope.ScalarTargetAsync<string>("SELECT CODE FROM dbo.WORKER_PARENTS WHERE ID = 2"));
        Assert.Equal(2, await scope.ScalarTargetAsync<int>("SELECT PARENT_ID FROM dbo.WORKER_CHILDREN WHERE ID = 1"));
    }

    [Fact]
    public async Task Seal_WhenAPlannedRowCollidesWithADifferentTargetRowOnAUniqueKey_RefusesNamingBothRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_users (id int NOT NULL CONSTRAINT PK_worker_users PRIMARY KEY, login nvarchar(64) NOT NULL CONSTRAINT UQ_worker_users_login UNIQUE);";
        await scope.ExecuteAsync(ddl + " INSERT dbo.worker_users VALUES (2001, N'alice'), (2002, N'bob');");
        // The target already knows alice under a different id: verbatim keys cannot merge the two.
        await scope.ExecuteTargetAsync(ddl + " INSERT dbo.worker_users VALUES (99, N'alice');");

        var exception = await Assert.ThrowsAsync<UniqueKeyCollisionException>(() =>
            RunTransferAsync(scope, "worker_users", "PK_worker_users", "SELECT * FROM dbo.worker_users")
        );

        Assert.Contains(
            "1 row(s) of dbo.worker_users on unique key (login), for example id=2001 (source) -> id=99 (target)",
            exception.Message
        );
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_users"));
    }

    [Fact]
    public async Task Transfer_WhenTwoTablesReferenceEachOther_FillsTheNullableSideAfterTheRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE dbo.worker_teams (id int NOT NULL CONSTRAINT PK_worker_teams PRIMARY KEY, lead_id int NULL);"
            + " CREATE TABLE dbo.worker_people (id int NOT NULL CONSTRAINT PK_worker_people PRIMARY KEY, team_id int NOT NULL CONSTRAINT FK_worker_people_team FOREIGN KEY REFERENCES dbo.worker_teams(id));"
            + " ALTER TABLE dbo.worker_teams ADD CONSTRAINT FK_worker_teams_lead FOREIGN KEY (lead_id) REFERENCES dbo.worker_people(id);";
        // 2,100 teams, each led by one of 2,100 people: neither table can go first while lead_id carries a value.
        const string numbers =
            "WITH n AS (SELECT TOP (2100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)";
        await scope.ExecuteAsync(
            ddl
                + " "
                + numbers
                + " INSERT dbo.worker_teams (id, lead_id) SELECT i, NULL FROM n; "
                + numbers
                + " INSERT dbo.worker_people (id, team_id) SELECT i, i FROM n; UPDATE dbo.worker_teams SET lead_id = id;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(
            scope,
            "worker_people",
            "PK_worker_people",
            "SELECT * FROM dbo.worker_people",
            // A planned team appears in the target after sealing: it is skipped, and the second phase must not
            // touch its deferred column either.
            afterSeal: (_, _) => scope.ExecuteTargetAsync("INSERT dbo.worker_teams (id, lead_id) VALUES (2100, NULL);")
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2100, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_people"));
        Assert.Equal(
            2099,
            await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_teams WHERE lead_id = id")
        );
        Assert.Equal(
            1,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM dbo.worker_teams WHERE id = 2100 AND lead_id IS NULL"
            )
        );
        Assert.Equal(
            2,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_worker_people_team', 'FK_worker_teams_lead') AND is_disabled = 0 AND is_not_trusted = 0"
            )
        );
    }

    [Fact]
    public async Task Start_WhenThePlanWasSealedByAnOlderAlgorithm_RefusesBeforeWritingAnything()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl = "CREATE TABLE dbo.worker_stale (id int NOT NULL CONSTRAINT PK_worker_stale PRIMARY KEY);";
        await scope.ExecuteAsync(ddl + " INSERT dbo.worker_stale VALUES (1),(2);");
        await scope.ExecuteTargetAsync(ddl);

        var exception = await Assert.ThrowsAsync<StalePlanException>(() =>
            RunTransferAsync(
                scope,
                "worker_stale",
                "PK_worker_stale",
                "SELECT * FROM dbo.worker_stale",
                afterSeal: (provider, planId) =>
                {
                    // The stored content is what an older build left behind: no sealing version at all.
                    using var control = provider.GetRequiredService<ControlDatabase>().Open();
                    control.Execute(
                        "UPDATE Plans SET ContentJson = REPLACE(ContentJson, '\"SealingVersion\":' || @current, '\"SealingVersion\":0') WHERE PlanId = @planId",
                        new ControlParameter("current", TransferPlanContent.CurrentSealingVersion.ToString()),
                        new ControlParameter("planId", planId.ToString())
                    );
                    return Task.CompletedTask;
                }
            )
        );

        Assert.Equal(0, exception.SealingVersion);
        Assert.Contains("Seal the plan again before starting a transfer.", exception.Message);
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_stale"));
    }

    [Fact]
    public async Task Seal_WhenAPlannedTableReferencesATableTheLoginCannotSee_RefusesInsteadOfDroppingTheEdge()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "EXEC('CREATE SCHEMA vault'); CREATE TABLE vault.worker_parents (id int NOT NULL CONSTRAINT PK_vault_parents PRIMARY KEY);"
                + " CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_children_vault_parents FOREIGN KEY REFERENCES vault.worker_parents(id));"
                + " INSERT vault.worker_parents VALUES (2); INSERT dbo.worker_children VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "EXEC('CREATE SCHEMA vault'); CREATE TABLE vault.worker_parents (id int NOT NULL CONSTRAINT PK_vault_parents PRIMARY KEY);"
                + " CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL CONSTRAINT FK_children_vault_parents FOREIGN KEY REFERENCES vault.worker_parents(id));"
        );
        var restricted = await RestrictedSourceLoginAsync(scope, "dbo.worker_children");

        var exception = await Assert.ThrowsAsync<IncompleteGraphException>(() =>
            RunTransferAsync(
                scope,
                "worker_children",
                "PK_worker_children",
                "SELECT * FROM dbo.worker_children",
                sourceConnectionString: restricted
            )
        );

        Assert.Contains("FK_children_vault_parents on dbo.worker_children references ?.#", exception.Message);
        Assert.Contains("which the source login cannot read", exception.Message);
    }

    [Fact]
    public async Task Seal_WhenSourceRowsReferenceAMissingParentAndTheTargetEnforcesTheKey_Refuses()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string tables =
            "CREATE TABLE dbo.worker_parents (id int NOT NULL CONSTRAINT PK_worker_parents PRIMARY KEY); CREATE TABLE dbo.worker_children (id int NOT NULL CONSTRAINT PK_worker_children PRIMARY KEY, parent_id int NOT NULL);";
        // The source constraint was added WITH NOCHECK, so an orphan slipped in; the target checks the same key.
        await scope.ExecuteAsync(
            tables
                + " INSERT dbo.worker_children VALUES (1,999); ALTER TABLE dbo.worker_children WITH NOCHECK ADD CONSTRAINT FK_worker_children_parents FOREIGN KEY (parent_id) REFERENCES dbo.worker_parents(id);"
        );
        await scope.ExecuteTargetAsync(
            tables
                + " ALTER TABLE dbo.worker_children ADD CONSTRAINT FK_worker_children_parents FOREIGN KEY (parent_id) REFERENCES dbo.worker_parents(id);"
        );

        var exception = await Assert.ThrowsAsync<SourceOrphansException>(() =>
            RunTransferAsync(scope, "worker_children", "PK_worker_children", "SELECT * FROM dbo.worker_children")
        );

        Assert.Contains(
            "1 planned row(s) in dbo.worker_children reference a dbo.worker_parents row through FK_worker_children_parents that does not exist in the source",
            exception.Message
        );
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM dbo.worker_children"));
    }

    [Fact]
    public async Task Transfer_WhenTheGraphNestsCyclesWithNullableBreaks_WritesEveryRowInAnOrderTheTargetAccepts()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(TwelveTableGraph.Source(nullableT11ToT12: true, nullableT12ToT11: false));
        await scope.ExecuteTargetAsync(TwelveTableGraph.Target(nullableT11ToT12: true, nullableT12ToT11: false));

        var job = await RunTransferAsync(
            scope,
            "T12",
            "PK_T12",
            "SELECT * FROM SchemaB.T12",
            businessSchema: "SchemaB",
            keyColumn: "Id"
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        foreach (
            var table in new[]
            {
                "SchemaA.T3",
                "SchemaA.T2",
                "SchemaA.T1",
                "SchemaB.T10",
                "SchemaB.T11",
                "SchemaB.T9",
                "SchemaB.T12",
            }
        )
            Assert.Equal(2, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM " + table));
        // Tables the root never demands stay untouched, and the deferred columns carry the source values.
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM SchemaA.T4"));
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM SchemaC.T8"));
        Assert.Equal(
            await scope.ScalarAsync<int>("SELECT SUM(T12IdA * 10 + T12IdB) FROM SchemaB.T11"),
            await scope.ScalarTargetAsync<int>("SELECT SUM(T12IdA * 10 + T12IdB) FROM SchemaB.T11")
        );
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT SelfId FROM SchemaB.T12 WHERE Id = 2"));
        Assert.Equal(
            0,
            await scope.ScalarTargetAsync<int>(
                "SELECT COUNT(*) FROM sys.foreign_keys f JOIN sys.schemas s ON s.schema_id = f.schema_id WHERE s.name IN ('SchemaA','SchemaB','SchemaC') AND (f.is_disabled = 1 OR f.is_not_trusted = 1)"
            )
        );
    }

    [Fact]
    public async Task Seal_WhenACycleHasNoNullableColumn_RefusesNamingTheCycle()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(TwelveTableGraph.Source(nullableT11ToT12: false, nullableT12ToT11: true));
        await scope.ExecuteTargetAsync(TwelveTableGraph.Target(nullableT11ToT12: false, nullableT12ToT11: true));

        var exception = await Assert.ThrowsAsync<UnorderablePlanException>(() =>
            RunTransferAsync(
                scope,
                "T12",
                "PK_T12",
                "SELECT * FROM SchemaB.T12",
                businessSchema: "SchemaB",
                keyColumn: "Id"
            )
        );

        // T12 -> T9 -> T11 -> T12 has no nullable edge once T11's columns are NOT NULL; T12 -> T11 being nullable
        // does not help because that edge is not the one closing this cycle.
        Assert.Contains("FK_T12_T9", exception.Message);
        Assert.Contains("FK_T9_T11", exception.Message);
        Assert.Contains("FK_T11_T12A", exception.Message);
        Assert.Contains("SchemaB.T12", exception.Message);
        Assert.Equal(0, await scope.ScalarTargetAsync<int>("SELECT COUNT(*) FROM SchemaB.T12"));
    }

    /// <summary>A source login that can read only the named table: the catalog then cannot resolve its parents.</summary>
    private static async Task<string> RestrictedSourceLoginAsync(SqlServerClosureScope scope, string table)
    {
        var login = "dp_e2e_" + Guid.NewGuid().ToString("N");
        const string password = "DataPitcherProbe!2026";
        await using (var server = new SqlConnection(scope.SourceAdminConnectionString))
        {
            await server.OpenAsync();
            await using var create = new SqlCommand($"CREATE LOGIN [{login}] WITH PASSWORD = '{password}';", server);
            await create.ExecuteNonQueryAsync();
        }
        await scope.ExecuteAsync(
            $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON OBJECT::{table} TO [{login}];"
        );
        return new SqlConnectionStringBuilder(scope.SourceConnectionString)
        {
            UserID = login,
            Password = password,
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>Scans, selects every row of <paramref name="rootTable"/>, seals, runs the worker and waits for the end.</summary>
    private static async Task<TransferJob> RunTransferAsync(
        SqlServerClosureScope scope,
        string rootTable,
        string primaryKey,
        string sql,
        Func<IServiceProvider, Guid, Task>? afterSeal = null,
        string? sourceConnectionString = null,
        string businessSchema = "dbo",
        string keyColumn = "id"
    )
    {
        await EnableSnapshotIsolationAsync(scope.SourceAdminConnectionString, scope.Database);
        await EnableSnapshotIsolationAsync(scope.TargetAdminConnectionString, scope.Database);
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-job-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceSecret = Path.Combine(directory, "source");
            var targetSecret = Path.Combine(directory, "target");
            await File.WriteAllTextAsync(sourceSecret, sourceConnectionString ?? scope.SourceConnectionString);
            await File.WriteAllTextAsync(targetSecret, scope.TargetConnectionString);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ControlDatabase:Path"] = Path.Combine(directory, "control.db"),
                        ["Secrets:Root"] = directory,
                        ["Worker:LeaseTtl"] = "00:00:30",
                        ["Worker:PollInterval"] = "00:00:00.050",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddDataPitcherComposition(configuration);
            using var provider = services.BuildServiceProvider();
            provider.ApplyControlDatabaseMigrations();
            var profiles = provider.GetRequiredService<ConnectionProfileStore>();
            var source = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "source",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    businessSchema,
                    "dbo"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "sqlserver",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    businessSchema,
                    "dbo"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            await provider
                .GetServices<IHostedService>()
                .OfType<SchemaScanWorker>()
                .Single()
                .ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(completedScan.State == SchemaScanState.Completed, completedScan.FailureDetail);
            var selectionId = Guid.NewGuid();
            await provider
                .GetRequiredService<SelectionStore>()
                .SaveAsync(
                    selectionId,
                    rootTable,
                    JsonSerializer.Serialize(
                        new
                        {
                            Mode = "raw",
                            RawSql = sql,
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    completedScan.SnapshotId,
                    businessSchema,
                    rootTable,
                    primaryKey,
                    [keyColumn]
                );
            var application = provider.GetRequiredService<IDataPitcherApplication>();
            var planId = Guid.NewGuid();
            await application.SavePlanAsync(
                planId,
                new SavePlanRequest(
                    "worker plan",
                    null,
                    "\"0\"",
                    selectionId,
                    source.ConnectionId,
                    target.ConnectionId
                ),
                CancellationToken.None
            );
            await application.QueuePlanSealAsync(planId, CancellationToken.None);
            if (afterSeal is not null)
                await afterSeal(provider, planId);
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                return await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<TransferJob> WaitForTerminalAsync(JobStore jobs, Guid jobId)
    {
        var last = jobs.Get(jobId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            last = jobs.Get(jobId);
            if (
                last.State is JobState.Cancelled or JobState.Succeeded or JobState.Failed or JobState.VerificationFailed
            )
                return last;
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not reach a terminal state. Last observed state: " + last.State + ".");
    }

    private static async Task EnableSnapshotIsolationAsync(string connectionString, string database)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "ALTER DATABASE [" + database + "] SET ALLOW_SNAPSHOT_ISOLATION ON;",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }
}
