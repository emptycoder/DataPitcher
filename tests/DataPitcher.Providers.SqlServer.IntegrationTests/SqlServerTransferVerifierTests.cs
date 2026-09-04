using System.Data;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Transfer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerTransferVerifierTests(SqlServerClosureFixture fixture)
{
    private static readonly TableAddress Children = new("dbo", "verify_children");
    private static readonly TableAddress Parents = new("dbo", "verify_parents");
    private static readonly TableAddress CompositeChildren = new("dbo", "verify_composite_children");
    private static readonly TableAddress CompositeParents = new("dbo", "verify_composite_parents");

    [Fact]
    public async Task VerifyAsync_WhenTheRunMatchesThePlanAndEveryKeyResolves_Passes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = SqlServerTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 1));

        await new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
            Plan(2, VerificationStrategy.StrictExact),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenTheRunMovedFewerRowsThanThePlanSealed_FailsNamingTheTable()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = SqlServerTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
                Plan(3, VerificationStrategy.StrictExact),
                context,
                CancellationToken.None
            )
        );

        Assert.Contains("dbo.verify_children: the plan sealed 3 row(s) but the run moved 1", exception.Message);
    }

    [Fact]
    public async Task VerifyAsync_WhenAPlannedRowIsNoLongerInTheTarget_FailsNamingTheTable()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        var context = SqlServerTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 1));
        await scope.ExecuteTargetAsync("DELETE dbo.verify_children WHERE id = 2;");

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
                Plan(2, VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal("1 planned row(s) of dbo.verify_children are not in the target.", exception.Message);
    }

    [Fact]
    public async Task VerifyAsync_WhenAWrittenRowReferencesAParentTheTargetDoesNotHave_FailsNamingTheRelationship()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateTablesAsync(scope);
        // A disabled target constraint lets the dangling row in; verification is what catches it.
        await scope.ExecuteTargetAsync(
            "ALTER TABLE dbo.verify_children NOCHECK CONSTRAINT FK_verify_children_parents;"
        );
        var context = SqlServerTransferTestData.Context();
        await ApplyAsync(scope, context, (1, 1), (2, 99));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
                Plan(2, VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "1 row(s) this run wrote to dbo.verify_children reference a dbo.verify_parents row through FK_verify_children_parents that is not in the target.",
            exception.Message
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenEveryReferenceIsNullAndTheParentTableIsNotOnTheTarget_Passes()
    {
        await using var scope = await fixture.CreateScopeAsync();
        // No parent table at all: a NULL reference has nothing to resolve, so the run never needed it.
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.verify_children (id int NOT NULL CONSTRAINT PK_verify_children PRIMARY KEY, parent_id int NULL);"
        );
        var context = SqlServerTransferTestData.Context();
        await ApplyAsync(scope, context, (1, null), (2, null));

        await new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
            Plan(2, VerificationStrategy.Standard),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenACompositeKeyReferencesParentColumnsOutOfTheirCatalogOrder_PairsThemByTheForeignKey()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateCompositeTablesAsync(scope);
        var context = SqlServerTransferTestData.Context();
        await ApplyCompositeAsync(scope, context, (1, 2, 1));

        await new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
            CompositePlan(VerificationStrategy.Standard),
            context,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task VerifyAsync_WhenACompositeKeyOnlyMatchesTheParentInCatalogOrder_FailsNamingTheRelationship()
    {
        await using var scope = await fixture.CreateScopeAsync();
        await CreateCompositeTablesAsync(scope);
        var context = SqlServerTransferTestData.Context();
        await ApplyCompositeAsync(scope, context, (1, 1, 2));

        var exception = await Assert.ThrowsAsync<TransferVerificationException>(() =>
            new SqlServerTransferVerifier(scope.TargetConnectionString).VerifyAsync(
                CompositePlan(VerificationStrategy.Standard),
                context,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "1 row(s) this run wrote to dbo.verify_composite_children reference a dbo.verify_composite_parents row through FK_verify_composite that is not in the target.",
            exception.Message
        );
    }

    private static async Task CreateTablesAsync(SqlServerClosureScope scope)
    {
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.verify_parents (id int NOT NULL CONSTRAINT PK_verify_parents PRIMARY KEY);"
                + " CREATE TABLE dbo.verify_children (id int NOT NULL CONSTRAINT PK_verify_children PRIMARY KEY, parent_id int NULL CONSTRAINT FK_verify_children_parents FOREIGN KEY REFERENCES dbo.verify_parents(id));"
                + " INSERT dbo.verify_parents VALUES (1);"
        );
    }

    /// <summary>The key is declared (b, a) while the columns sit (a, b); the child has no constraint so verification alone judges it.</summary>
    private static async Task CreateCompositeTablesAsync(SqlServerClosureScope scope)
    {
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.verify_composite_parents (a int NOT NULL, b int NOT NULL, CONSTRAINT PK_verify_composite_parents PRIMARY KEY (b, a));"
                + " CREATE TABLE dbo.verify_composite_children (id int NOT NULL CONSTRAINT PK_verify_composite_children PRIMARY KEY, x int NULL, y int NULL);"
                + " INSERT dbo.verify_composite_parents VALUES (1, 2);"
        );
    }

    private static async Task ApplyCompositeAsync(
        SqlServerClosureScope scope,
        SqlServerExecutionContext context,
        params (int Id, int X, int Y)[] rows
    )
    {
        var batch = new SqlServerTransferBatch(
            0,
            rows.Select(row => new SqlServerTransferRow(
                new StableKey([new KeyComponent("id", row.Id)]),
                new Dictionary<string, object?>
                {
                    ["id"] = row.Id,
                    ["x"] = row.X,
                    ["y"] = row.Y,
                }
            )),
            new StableKey([new KeyComponent("id", rows[^1].Id)]),
            SqlServerConflictPolicy.SkipExisting
        );
        await new SqlServerTransferExecutor(scope.TargetConnectionString, new Mirror(), new Barrier()).ExecuteAsync(
            context,
            new SqlServerWriteTable(
                CompositeChildren,
                [
                    new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
                    new("x", "int", typeof(int), SqlDbType.Int, false, false, false, false, true, null),
                    new("y", "int", typeof(int), SqlDbType.Int, false, false, false, false, true, null),
                ]
            ),
            batch,
            CancellationToken.None
        );
    }

    private static async Task ApplyAsync(
        SqlServerClosureScope scope,
        SqlServerExecutionContext context,
        params (int Id, int? Parent)[] rows
    )
    {
        var batch = new SqlServerTransferBatch(
            0,
            rows.Select(row => new SqlServerTransferRow(
                new StableKey([new KeyComponent("id", row.Id)]),
                new Dictionary<string, object?> { ["id"] = row.Id, ["parent_id"] = row.Parent }
            )),
            new StableKey([new KeyComponent("id", rows[^1].Id)]),
            SqlServerConflictPolicy.SkipExisting
        );
        await new SqlServerTransferExecutor(scope.TargetConnectionString, new Mirror(), new Barrier()).ExecuteAsync(
            context,
            Table(),
            batch,
            CancellationToken.None
        );
    }

    private static SqlServerWriteTable Table() =>
        new(
            Children,
            [
                new("id", "int", typeof(int), SqlDbType.Int, true, false, false, false, false, null),
                new("parent_id", "int", typeof(int), SqlDbType.Int, false, false, false, false, true, null),
            ]
        );

    /// <summary>The parent table is deliberately absent from the plan: its rows already exist in the target.</summary>
    private static TransferPlanContent Plan(long plannedChildren, VerificationStrategy verification) =>
        Plan(
            Children,
            new RelationshipPolicy(
                "FK_verify_children_parents",
                Children,
                Parents,
                ["parent_id"],
                ["id"],
                RelationshipDirection.Outbound,
                true
            ),
            ["id", "parent_id"],
            plannedChildren,
            verification
        );

    private static TransferPlanContent CompositePlan(VerificationStrategy verification) =>
        Plan(
            CompositeChildren,
            new RelationshipPolicy(
                "FK_verify_composite",
                CompositeChildren,
                CompositeParents,
                ["x", "y"],
                ["b", "a"],
                RelationshipDirection.Outbound,
                true
            ),
            ["id", "x", "y"],
            1,
            verification
        );

    private static TransferPlanContent Plan(
        TableAddress children,
        RelationshipPolicy relationship,
        string[] columns,
        long plannedChildren,
        VerificationStrategy verification
    ) =>
        new(
            new ConnectionFingerprint("sqlserver", "source", "source"),
            new ConnectionFingerprint("sqlserver", "target", "target"),
            new SchemaSnapshotReference("source"),
            new SchemaSnapshotReference("target"),
            [],
            [relationship],
            [],
            ConsistencyMode.FrozenKeys,
            TransferMode.ResumableStaged,
            TriggerStrategy.Fire,
            ConstraintStrategy.Enforce,
            [new StableKeyDefinition(children, "PK_" + children.Name, ["id"])],
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

    private sealed class Mirror : ISqlServerDerivedCheckpointMirror
    {
        public Task WriteAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Barrier : ISqlServerAfterTargetCommitBarrier
    {
        public Task WaitAsync(SqlServerTargetCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
