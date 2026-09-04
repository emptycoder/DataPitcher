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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlJobWorkerEndToEndTests(PostgreSqlClosureFixture fixture)
    : IClassFixture<PostgreSqlClosureFixture>
{
    [Fact]
    public async Task Transfer_WhenPlanIsSealedAndJobStarts_CopiesReferentiallyRequiredRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY); "
                + "CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES worker_parents(id)); "
                + "INSERT INTO worker_parents VALUES (2); INSERT INTO worker_children VALUES (1,2);"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY); "
                + "CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES worker_parents(id));"
        );
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-pg-worker-" + Guid.NewGuid().ToString("N"));
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
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    scope.Schema,
                    "datapitcher"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(
                completedScan.State == SchemaScanState.Completed,
                "Schema scan ended in " + completedScan.State + " (" + completedScan.FailureCode + ")."
            );
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
                            RawSql = "SELECT id AS \"__datapitcher_key_0\" FROM worker_children",
                            Parameters = Array.Empty<object>(),
                        }
                    ),
                    "\"0\"",
                    CancellationToken.None,
                    source.ConnectionId,
                    snapshotId,
                    scope.Schema,
                    "worker_children",
                    "pk_worker_children",
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
            await scope.ExecuteTargetAsync("INSERT INTO worker_parents VALUES (2);");
            var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
            Assert.Equal("sealed", review.Seal.Status);
            Assert.Equal(2, review.Totals.PlannedWrites);
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
                        + " ("
                        + job.FailureCode
                        + "). Events: "
                        + string.Join(", ", events.Events.Select(jobEvent => jobEvent.Payload.State))
                );
                Assert.Equal(
                    1L,
                    await scope.ScalarTargetAsync<long>(
                        "SELECT COUNT(*) FROM worker_children WHERE id = 1 AND parent_id = 2"
                    )
                );
                Assert.Equal(
                    1L,
                    await scope.ScalarTargetAsync<long>("SELECT COUNT(*) FROM worker_parents WHERE id = 2")
                );
                // The parent already existed in the target: it is skipped and reported, never a failure.
                var conflict = Assert.Single(events.Events, jobEvent => jobEvent.EventType == "conflict");
                Assert.Contains("worker_parents", conflict.Payload.Detail, StringComparison.Ordinal);
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
            "CREATE TABLE worker_nodes (id integer NOT NULL CONSTRAINT pk_worker_nodes PRIMARY KEY, parent_id integer NULL CONSTRAINT fk_worker_nodes_parent REFERENCES worker_nodes(id));";
        // A root that is its own parent with 2,000 children the 2,000-row batches only reach after it, plus a two-way
        // cycle (2002 <-> 2003) with rows below it that no levelling can reach.
        await scope.ExecuteAsync(
            ddl
                + " INSERT INTO worker_nodes (id, parent_id) VALUES (2001, 2001); INSERT INTO worker_nodes (id, parent_id) SELECT g, 2001 FROM generate_series(1, 2000) g;"
                + " INSERT INTO worker_nodes (id, parent_id) VALUES (2002, NULL), (2003, 2002); UPDATE worker_nodes SET parent_id = 2003 WHERE id = 2002;"
                + " INSERT INTO worker_nodes (id, parent_id) VALUES (2004, 2003), (2005, 2004);"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_nodes", "pk_worker_nodes", "SELECT * FROM worker_nodes");

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2005L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_nodes"));
        Assert.Equal(
            0L,
            await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_nodes WHERE parent_id IS NULL")
        );
        Assert.Equal(
            2000L * 2001 + 2001 + 2003 + 2002 + 2003 + 2004,
            await scope.ScalarTargetAsync<long>("SELECT sum(parent_id) FROM worker_nodes")
        );
    }

    [Fact]
    public async Task Transfer_WhenATableReferencesItselfThroughAUniqueKeyThatIsNotTheStableKey_WritesAncestorsFirst()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE worker_codes (id integer NOT NULL CONSTRAINT pk_worker_codes PRIMARY KEY, code text NOT NULL CONSTRAINT uq_worker_codes UNIQUE, parent_code text NULL CONSTRAINT fk_worker_codes_parent REFERENCES worker_codes(code));";
        // The stable key is id, the parent link goes through code: every child has a lower id than its parent.
        await scope.ExecuteAsync(
            ddl
                + " INSERT INTO worker_codes (id, code, parent_code) VALUES (2001, 'root', NULL); INSERT INTO worker_codes (id, code, parent_code) SELECT g, 'c' || g, 'root' FROM generate_series(1, 2000) g;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(scope, "worker_codes", "pk_worker_codes", "SELECT * FROM worker_codes");

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2001L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_codes"));
        Assert.Equal(
            2000L,
            await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_codes WHERE parent_code = 'root'")
        );
    }

    [Fact]
    public async Task Transfer_WhenTheTargetDeclaresTablesAndColumnsInAnotherCase_MapsThemAnyway()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY, code text NOT NULL);"
                + " CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES worker_parents(id));"
                + " INSERT INTO worker_parents VALUES (2, 'two'); INSERT INTO worker_children VALUES (1, 2);"
        );
        // Quoted upper-case identifiers are distinct spellings in PostgreSQL; the transfer has to use the target's own.
        await scope.ExecuteTargetAsync(
            "CREATE TABLE \"WORKER_PARENTS\" (\"ID\" integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY, \"CODE\" text NOT NULL);"
                + " CREATE TABLE \"WORKER_CHILDREN\" (\"ID\" integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, \"PARENT_ID\" integer NOT NULL CONSTRAINT fk_worker_children_parents REFERENCES \"WORKER_PARENTS\"(\"ID\"));"
        );

        var job = await RunTransferAsync(
            scope,
            "worker_children",
            "pk_worker_children",
            "SELECT * FROM worker_children"
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(
            "two",
            await scope.ScalarTargetAsync<string>("SELECT \"CODE\" FROM \"WORKER_PARENTS\" WHERE \"ID\" = 2")
        );
        Assert.Equal(
            2,
            await scope.ScalarTargetAsync<int>("SELECT \"PARENT_ID\" FROM \"WORKER_CHILDREN\" WHERE \"ID\" = 1")
        );
    }

    [Fact]
    public async Task Reseal_WhenASourceRowWasAddedAfterATransfer_SkipsOnlyTheRowTheTargetAlreadyHas()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE worker_rows (id integer NOT NULL CONSTRAINT pk_worker_rows PRIMARY KEY, code text NOT NULL);"
                + " INSERT INTO worker_rows VALUES (1, 'one');"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE worker_rows (id integer NOT NULL CONSTRAINT pk_worker_rows PRIMARY KEY, code text NOT NULL);"
        );

        var job = await RunTransferAsync(
            scope,
            "worker_rows",
            "pk_worker_rows",
            "SELECT * FROM worker_rows",
            afterRun: async (provider, planId, first) =>
            {
                Assert.True(first.State == JobState.Succeeded, first.FailureCode + ": " + first.FailureDetail);
                await scope.ExecuteAsync("INSERT INTO worker_rows VALUES (2, 'two');");
                var application = provider.GetRequiredService<IDataPitcherApplication>();
                await application.QueuePlanSealAsync(planId, CancellationToken.None);
                var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
                Assert.Equal("sealed", review.Seal.Status);
                var skipped = Assert.Single(review.Warnings, warning => warning.Code == "roots_skipped");
                Assert.StartsWith("1 of 2 selected row(s)", skipped.Message);
                var started = await application.StartJobAsync(planId, "worker-job-2", CancellationToken.None);
                return await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );
            }
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal("two", await scope.ScalarTargetAsync<string>("SELECT code FROM worker_rows WHERE id = 2"));
    }

    [Fact]
    public async Task Reseal_WhenTheTargetRowWasDeletedAfterATransfer_PlansAndWritesItAgain()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "CREATE TABLE worker_rows (id integer NOT NULL CONSTRAINT pk_worker_rows PRIMARY KEY, code text NOT NULL);"
                + " INSERT INTO worker_rows VALUES (1, 'one');"
        );
        await scope.ExecuteTargetAsync(
            "CREATE TABLE worker_rows (id integer NOT NULL CONSTRAINT pk_worker_rows PRIMARY KEY, code text NOT NULL);"
        );

        var job = await RunTransferAsync(
            scope,
            "worker_rows",
            "pk_worker_rows",
            "SELECT * FROM worker_rows",
            afterRun: async (provider, planId, first) =>
            {
                Assert.True(first.State == JobState.Succeeded, first.FailureCode + ": " + first.FailureDetail);
                await scope.ExecuteTargetAsync("DELETE FROM worker_rows WHERE id = 1;");
                var application = provider.GetRequiredService<IDataPitcherApplication>();
                await application.QueuePlanSealAsync(planId, CancellationToken.None);
                var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
                Assert.Equal("sealed", review.Seal.Status);
                Assert.DoesNotContain(review.Warnings, warning => warning.Code == "roots_skipped");
                var started = await application.StartJobAsync(planId, "worker-job-2", CancellationToken.None);
                return await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );
            }
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal("one", await scope.ScalarTargetAsync<string>("SELECT code FROM worker_rows WHERE id = 1"));
    }

    [Fact]
    public async Task Seal_WhenAPlannedRowCollidesWithADifferentTargetRowOnAUniqueKey_RefusesNamingBothRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE worker_users (id integer NOT NULL CONSTRAINT pk_worker_users PRIMARY KEY, login text NOT NULL CONSTRAINT uq_worker_users_login UNIQUE);";
        await scope.ExecuteAsync(ddl + " INSERT INTO worker_users VALUES (2001, 'alice'), (2002, 'bob');");
        // The target already knows alice under a different id: verbatim keys cannot merge the two.
        await scope.ExecuteTargetAsync(ddl + " INSERT INTO worker_users VALUES (99, 'alice');");

        var exception = await Assert.ThrowsAsync<UniqueKeyCollisionException>(() =>
            RunTransferAsync(scope, "worker_users", "pk_worker_users", "SELECT * FROM worker_users")
        );

        Assert.Contains(
            "1 row(s) of "
                + scope.Schema
                + ".worker_users on unique key (login), for example id=2001 (source) -> id=99 (target)",
            exception.Message
        );
        Assert.Equal(1L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_users"));
    }

    [Fact]
    public async Task Transfer_WhenTwoTablesReferenceEachOther_FillsTheNullableSideAfterTheRows()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl =
            "CREATE TABLE worker_teams (id integer NOT NULL CONSTRAINT pk_worker_teams PRIMARY KEY, lead_id integer NULL);"
            + " CREATE TABLE worker_people (id integer NOT NULL CONSTRAINT pk_worker_people PRIMARY KEY, team_id integer NOT NULL CONSTRAINT fk_worker_people_team REFERENCES worker_teams(id));"
            + " ALTER TABLE worker_teams ADD CONSTRAINT fk_worker_teams_lead FOREIGN KEY (lead_id) REFERENCES worker_people(id);";
        // 2,100 teams, each led by one of 2,100 people: neither table can go first while lead_id carries a value.
        await scope.ExecuteAsync(
            ddl
                + " INSERT INTO worker_teams (id, lead_id) SELECT g, NULL FROM generate_series(1, 2100) g;"
                + " INSERT INTO worker_people (id, team_id) SELECT g, g FROM generate_series(1, 2100) g;"
                + " UPDATE worker_teams SET lead_id = id;"
        );
        await scope.ExecuteTargetAsync(ddl);

        var job = await RunTransferAsync(
            scope,
            "worker_people",
            "pk_worker_people",
            "SELECT * FROM worker_people",
            // A planned team appears in the target after sealing: it is skipped, and the second phase must not
            // touch its deferred column either.
            afterSeal: (_, _) => scope.ExecuteTargetAsync("INSERT INTO worker_teams (id, lead_id) VALUES (2100, NULL);")
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        Assert.Equal(2100L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_people"));
        Assert.Equal(
            2099L,
            await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_teams WHERE lead_id = id")
        );
        Assert.Equal(
            1L,
            await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_teams WHERE id = 2100 AND lead_id IS NULL")
        );
    }

    [Fact]
    public async Task Start_WhenThePlanWasSealedByAnOlderAlgorithm_RefusesBeforeWritingAnything()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string ddl = "CREATE TABLE worker_stale (id integer NOT NULL CONSTRAINT pk_worker_stale PRIMARY KEY);";
        await scope.ExecuteAsync(ddl + " INSERT INTO worker_stale VALUES (1),(2);");
        await scope.ExecuteTargetAsync(ddl);

        var exception = await Assert.ThrowsAsync<StalePlanException>(() =>
            RunTransferAsync(
                scope,
                "worker_stale",
                "pk_worker_stale",
                "SELECT * FROM worker_stale",
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
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_stale"));
    }

    [Fact]
    public async Task Seal_WhenSourceRowsReferenceAMissingParentAndTheTargetEnforcesTheKey_Refuses()
    {
        await using var scope = await fixture.CreateScopeAsync();
        const string tables =
            "CREATE TABLE worker_parents (id integer NOT NULL CONSTRAINT pk_worker_parents PRIMARY KEY); CREATE TABLE worker_children (id integer NOT NULL CONSTRAINT pk_worker_children PRIMARY KEY, parent_id integer NOT NULL);";
        // The source constraint was added NOT VALID, so an orphan slipped in; the target checks the same key.
        await scope.ExecuteAsync(
            tables
                + " INSERT INTO worker_children VALUES (1,999); ALTER TABLE worker_children ADD CONSTRAINT fk_worker_children_parents FOREIGN KEY (parent_id) REFERENCES worker_parents(id) NOT VALID;"
        );
        await scope.ExecuteTargetAsync(
            tables
                + " ALTER TABLE worker_children ADD CONSTRAINT fk_worker_children_parents FOREIGN KEY (parent_id) REFERENCES worker_parents(id);"
        );

        var exception = await Assert.ThrowsAsync<SourceOrphansException>(() =>
            RunTransferAsync(scope, "worker_children", "pk_worker_children", "SELECT * FROM worker_children")
        );

        Assert.Contains(
            "1 planned row(s) in "
                + scope.Schema
                + ".worker_children reference a "
                + scope.Schema
                + ".worker_parents row through fk_worker_children_parents that does not exist in the source",
            exception.Message
        );
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM worker_children"));
    }

    [Fact]
    public async Task Transfer_WhenTheGraphNestsCyclesWithNullableBreaks_WritesEveryRowInAnOrderTheTargetAccepts()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var graph = new TwelveTableGraph(scope.Schema);
        await scope.ExecuteAsync(graph.Source(nullableT11ToT12: true, nullableT12ToT11: false));
        await scope.ExecuteTargetAsync(graph.Target(nullableT11ToT12: true, nullableT12ToT11: false));

        var job = await RunTransferAsync(
            scope,
            "t12",
            "pk_t12",
            "SELECT * FROM " + graph.B + ".t12",
            businessSchema: graph.B
        );

        Assert.True(job.State == JobState.Succeeded, job.FailureCode + ": " + job.FailureDetail);
        foreach (
            var table in new[]
            {
                graph.A + ".t3",
                graph.A + ".t2",
                graph.A + ".t1",
                graph.B + ".t10",
                graph.B + ".t11",
                graph.B + ".t9",
                graph.B + ".t12",
            }
        )
            Assert.Equal(2L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM " + table));
        // Tables the root never demands stay untouched, and the deferred columns carry the source values.
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM " + graph.A + ".t4"));
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM " + graph.C + ".t8"));
        Assert.Equal(
            await scope.ScalarAsync<long>("SELECT sum(t12ida * 10 + t12idb) FROM " + graph.B + ".t11"),
            await scope.ScalarTargetAsync<long>("SELECT sum(t12ida * 10 + t12idb) FROM " + graph.B + ".t11")
        );
        Assert.Equal(1, await scope.ScalarTargetAsync<int>("SELECT selfid FROM " + graph.B + ".t12 WHERE id = 2"));
        Assert.Equal(
            0L,
            await scope.ScalarTargetAsync<long>(
                "SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE c.contype = 'f' AND NOT c.convalidated AND n.nspname IN ('"
                    + graph.A
                    + "','"
                    + graph.B
                    + "','"
                    + graph.C
                    + "')"
            )
        );
    }

    [Fact]
    public async Task Seal_WhenACycleHasNoNullableColumn_RefusesNamingTheCycle()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var graph = new TwelveTableGraph(scope.Schema);
        await scope.ExecuteAsync(graph.Source(nullableT11ToT12: false, nullableT12ToT11: true));
        await scope.ExecuteTargetAsync(graph.Target(nullableT11ToT12: false, nullableT12ToT11: true));

        var exception = await Assert.ThrowsAsync<UnorderablePlanException>(() =>
            RunTransferAsync(scope, "t12", "pk_t12", "SELECT * FROM " + graph.B + ".t12", businessSchema: graph.B)
        );

        // t12 -> t9 -> t11 -> t12 has no nullable edge once t11's columns are NOT NULL; t12 -> t11 being nullable
        // does not help because that edge is not the one closing this cycle.
        Assert.Contains("fk_t12_t9", exception.Message);
        Assert.Contains("fk_t9_t11", exception.Message);
        Assert.Contains("fk_t11_t12a", exception.Message);
        Assert.Contains(graph.B + ".t12", exception.Message);
        Assert.Equal(0L, await scope.ScalarTargetAsync<long>("SELECT count(*) FROM " + graph.B + ".t12"));
    }

    private static async Task<TransferJob> RunTransferAsync(
        PostgreSqlClosureScope scope,
        string rootTable,
        string primaryKey,
        string sql,
        Func<IServiceProvider, Guid, Task>? afterSeal = null,
        string? businessSchema = null,
        Func<IServiceProvider, Guid, TransferJob, Task<TransferJob>>? afterRun = null
    )
    {
        businessSchema ??= scope.Schema;
        var directory = Path.Combine(Path.GetTempPath(), "datapitcher-pg-worker-" + Guid.NewGuid().ToString("N"));
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
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, sourceSecret),
                    businessSchema,
                    "datapitcher"
                ),
                "source",
                CancellationToken.None
            );
            var target = await profiles.CreateAsync(
                new ConnectionProfileDraft(
                    "target",
                    "postgresql",
                    new SecretReference(SecretReferenceKind.FileMounted, targetSecret),
                    businessSchema,
                    "datapitcher"
                ),
                "target",
                CancellationToken.None
            );
            var snapshots = provider.GetRequiredService<SchemaSnapshotStore>();
            var scan = await snapshots.QueueAsync(source.ConnectionId, "source-scan", CancellationToken.None);
            var scanner = provider.GetServices<IHostedService>().OfType<SchemaScanWorker>().Single();
            await scanner.ProcessNextAsync(CancellationToken.None);
            var completedScan = await snapshots.GetScanAsync(source.ConnectionId, scan.ScanId, CancellationToken.None);
            Assert.True(
                completedScan.State == SchemaScanState.Completed,
                "Schema scan ended in " + completedScan.State + " (" + completedScan.FailureCode + ")."
            );
            var snapshotId =
                completedScan.SnapshotId
                ?? throw new InvalidOperationException("Source schema scan did not create a snapshot.");
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
                    snapshotId,
                    businessSchema,
                    rootTable,
                    primaryKey,
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
            var review = await application.GetPlanReviewAsync(planId, CancellationToken.None);
            Assert.Equal("sealed", review.Seal.Status);
            if (afterSeal is not null)
                await afterSeal(provider, planId);
            var worker = provider.GetServices<IHostedService>().OfType<JobWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var started = await application.StartJobAsync(planId, "worker-job", CancellationToken.None);
                var started2 = started;
                var job = await WaitForTerminalAsync(
                    provider.GetRequiredService<JobStore>(),
                    started2.JobId ?? throw new InvalidOperationException("Job start did not return a job identifier.")
                );
                return afterRun is null ? job : await afterRun(provider, planId, job);
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
}
