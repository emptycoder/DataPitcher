using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using NpgsqlTypes;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

public sealed class PostgreSqlTransferVerifierTests(PostgreSqlClosureFixture fixture)
    : IClassFixture<PostgreSqlClosureFixture>
{
    [Fact]
    public async Task VerifyAsync_WhenTheRunMatchesThePlanAndEveryKeyResolves_Passes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = PostgreSqlTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 1));

        await new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
            Plan(scope.Schema, 2, VerificationStrategy.StrictExact),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenTheRunMovedFewerRowsThanThePlanSealed_FailsNamingTheTable()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = PostgreSqlTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
                Plan(scope.Schema, 3, VerificationStrategy.StrictExact),
                context,
                CancellationToken.None
            )
        );

        Assert.Contains(
            scope.Schema + ".verify_children: the plan sealed 3 row(s) but the run moved 1",
            exception.Message
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenAPlannedRowIsNoLongerInTheTarget_FailsNamingTheTable()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = PostgreSqlTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 1));
        await scope.ExecuteTargetAsync("DELETE FROM verify_children WHERE id = 2;");

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
                Plan(scope.Schema, 2, VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "1 planned row(s) of " + scope.Schema + ".verify_children are not in the target.",
            exception.Message
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenAWrittenRowReferencesAParentTheTargetDoesNotHave_FailsNamingTheRelationship()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        // Disabled constraint triggers let the dangling row in; verification is what catches it.
        await scope.ExecuteTargetAsync("ALTER TABLE verify_children DISABLE TRIGGER ALL;");
        var context = PostgreSqlTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 99));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
                Plan(scope.Schema, 2, VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "1 row(s) this run wrote to "
                + scope.Schema
                + ".verify_children reference a "
                + scope.Schema
                + ".verify_parents row through fk_verify_children_parents that is not in the target.",
            exception.Message
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenEveryReferenceIsNullAndTheParentTableIsNotOnTheTarget_Passes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        // No parent table at all: a NULL reference has nothing to resolve, so the run never needed it.
        await scope.ExecuteTargetAsync(
            "CREATE TABLE verify_children (id integer NOT NULL CONSTRAINT pk_verify_children PRIMARY KEY, parent_id integer NULL);"
        );
        var context = PostgreSqlTransferTestData.Context();
        await ApplyAsync(scope, context, (1, null), (2, null));

        await new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
            Plan(scope.Schema, 2, VerificationStrategy.Standard),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenACompositeKeyReferencesParentColumnsOutOfTheirCatalogOrder_PairsThemByTheForeignKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateCompositeTablesAsync(scope);
        var context = PostgreSqlTransferTestData.Context();
        await ApplyCompositeAsync(scope, context, (1, 2, 1));

        await new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
            CompositePlan(scope.Schema, VerificationStrategy.Standard),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenACompositeKeyOnlyMatchesTheParentInCatalogOrder_FailsNamingTheRelationship()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateCompositeTablesAsync(scope);
        var context = PostgreSqlTransferTestData.Context();
        await ApplyCompositeAsync(scope, context, (1, 1, 2));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new PostgreSqlTransferVerifier(scope.Target).VerifyAsync(
                CompositePlan(scope.Schema, VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "1 row(s) this run wrote to "
                + scope.Schema
                + ".verify_composite_children reference a "
                + scope.Schema
                + ".verify_composite_parents row through fk_verify_composite that is not in the target.",
            exception.Message
        );
    }

    /// <summary>The key is declared (b, a) while the columns sit (a, b); the child has no constraint so verification alone judges it.</summary>
    private static Task CreateCompositeTablesAsync(PostgreSqlClosureScope scope) =>
        scope.ExecuteTargetAsync(
            "CREATE TABLE verify_composite_parents (a integer NOT NULL, b integer NOT NULL, CONSTRAINT pk_verify_composite_parents PRIMARY KEY (b, a));"
                + " CREATE TABLE verify_composite_children (id integer NOT NULL CONSTRAINT pk_verify_composite_children PRIMARY KEY, x integer NULL, y integer NULL);"
                + " INSERT INTO verify_composite_parents VALUES (1, 2);"
        );

    private static async Task ApplyCompositeAsync(
        PostgreSqlClosureScope scope,
        PostgreSqlExecutionContext context,
        params (int Id, int X, int Y)[] rows
    )
    {
        var batch = new PostgreSqlTransferBatch(
            0,
            rows.Select(row => new PostgreSqlTransferRow(
                new StableKey([new KeyComponent("id", row.Id)]),
                new Dictionary<string, object?>
                {
                    ["id"] = row.Id,
                    ["x"] = row.X,
                    ["y"] = row.Y,
                }
            )),
            new StableKey([new KeyComponent("id", rows[^1].Id)]),
            PostgreSqlConflictPolicy.SkipExisting
        );
        await new PostgreSqlTransferExecutor(scope.Target, new Mirror(), new Barrier()).ExecuteAsync(
            context,
            new PostgreSqlWriteTable(
                new(scope.Schema, "verify_composite_children"),
                [
                    new("id", "integer", NpgsqlDbType.Integer, true, false, false, false, null),
                    new("x", "integer", NpgsqlDbType.Integer, false, false, false, false, null),
                    new("y", "integer", NpgsqlDbType.Integer, false, false, false, false, null),
                ]
            ),
            batch,
            CancellationToken.None
        );
    }

    private static Task CreateTablesAsync(PostgreSqlClosureScope scope) =>
        scope.ExecuteTargetAsync(
            "CREATE TABLE verify_parents (id integer NOT NULL CONSTRAINT pk_verify_parents PRIMARY KEY);"
                + " CREATE TABLE verify_children (id integer NOT NULL CONSTRAINT pk_verify_children PRIMARY KEY, parent_id integer NULL CONSTRAINT fk_verify_children_parents REFERENCES verify_parents(id));"
                + " INSERT INTO verify_parents VALUES (1);"
        );

    private static async Task ApplyAsync(
        PostgreSqlClosureScope scope,
        PostgreSqlExecutionContext context,
        params (int Id, int? Parent)[] rows
    )
    {
        var batch = new PostgreSqlTransferBatch(
            0,
            rows.Select(row => new PostgreSqlTransferRow(
                new StableKey([new KeyComponent("id", row.Id)]),
                new Dictionary<string, object?> { ["id"] = row.Id, ["parent_id"] = row.Parent }
            )),
            new StableKey([new KeyComponent("id", rows[^1].Id)]),
            PostgreSqlConflictPolicy.SkipExisting
        );
        await new PostgreSqlTransferExecutor(scope.Target, new Mirror(), new Barrier()).ExecuteAsync(
            context,
            Table(scope.Schema),
            batch,
            CancellationToken.None
        );
    }

    private static PostgreSqlWriteTable Table(string schema) =>
        new(
            new(schema, "verify_children"),
            [
                new("id", "integer", NpgsqlDbType.Integer, true, false, false, false, null),
                new("parent_id", "integer", NpgsqlDbType.Integer, false, false, false, false, null),
            ]
        );

    /// <summary>The parent table is deliberately absent from the plan: its rows already exist in the target.</summary>
    private static TransferPlanContent Plan(string schema, long plannedChildren, VerificationStrategy verification)
    {
        var children = new TableAddress(schema, "verify_children");
        return Plan(
            children,
            new RelationshipPolicy(
                "fk_verify_children_parents",
                children,
                new TableAddress(schema, "verify_parents"),
                ["parent_id"],
                ["id"],
                RelationshipDirection.Outbound,
                true
            ),
            ["id", "parent_id"],
            plannedChildren,
            verification
        );
    }

    private static TransferPlanContent CompositePlan(string schema, VerificationStrategy verification)
    {
        var children = new TableAddress(schema, "verify_composite_children");
        return Plan(
            children,
            new RelationshipPolicy(
                "fk_verify_composite",
                children,
                new TableAddress(schema, "verify_composite_parents"),
                ["x", "y"],
                ["b", "a"],
                RelationshipDirection.Outbound,
                true
            ),
            ["id", "x", "y"],
            1,
            verification
        );
    }

    private static TransferPlanContent Plan(
        TableAddress children,
        RelationshipPolicy relationship,
        string[] columns,
        long plannedChildren,
        VerificationStrategy verification
    ) =>
        new(
            new ConnectionFingerprint("postgresql", "source", "source"),
            new ConnectionFingerprint("postgresql", "target", "target"),
            new SchemaSnapshotReference("source"),
            new SchemaSnapshotReference("target"),
            [],
            [relationship],
            [],
            ConsistencyMode.FrozenKeys,
            TransferMode.ResumableStaged,
            TriggerStrategy.Fire,
            ConstraintStrategy.Enforce,
            [new StableKeyDefinition(children, "pk_" + children.Name, ["id"])],
            [
                new PlanTable(
                    new TableMapping(
                        children,
                        children,
                        columns.Select(column => new ColumnMapping(column, column)).ToArray()
                    ),
                    PlanTableState.Root,
                    new ManifestCounts(plannedChildren, plannedChildren, plannedChildren, 0),
                    new TopologicalGroup([children]),
                    CycleStrategy.NotApplicable
                ),
            ],
            new BatchTarget(2000, 8 * 1024 * 1024),
            verification,
            new ManifestCounts(plannedChildren, plannedChildren, plannedChildren, 0),
            TransferPlanContent.CurrentSealingVersion
        );

    private sealed class Mirror : IDerivedCheckpointMirror
    {
        public Task WriteAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Barrier : IAfterTargetCommitBarrier
    {
        public Task WaitAsync(PostgreSqlTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
