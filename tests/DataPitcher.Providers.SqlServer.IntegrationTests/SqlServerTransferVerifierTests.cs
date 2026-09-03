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

    private static async Task CreateTablesAsync(SqlServerClosureScope scope)
    {
        await scope.ExecuteTargetAsync(
            "CREATE TABLE dbo.verify_parents (id int NOT NULL CONSTRAINT PK_verify_parents PRIMARY KEY);"
                + " CREATE TABLE dbo.verify_children (id int NOT NULL CONSTRAINT PK_verify_children PRIMARY KEY, parent_id int NULL CONSTRAINT FK_verify_children_parents FOREIGN KEY REFERENCES dbo.verify_parents(id));"
                + " INSERT dbo.verify_parents VALUES (1);"
        );
    }

    private static async Task ApplyAsync(
        SqlServerClosureScope scope,
        SqlServerExecutionContext context,
        params (int Id, int Parent)[] rows
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
        new(
            new ConnectionFingerprint("sqlserver", "source", "source"),
            new ConnectionFingerprint("sqlserver", "target", "target"),
            new SchemaSnapshotReference("source"),
            new SchemaSnapshotReference("target"),
            [],
            [
                new RelationshipPolicy(
                    "FK_verify_children_parents",
                    Children,
                    Parents,
                    ["parent_id"],
                    ["id"],
                    RelationshipDirection.Outbound,
                    true
                ),
            ],
            [],
            ConsistencyMode.FrozenKeys,
            TransferMode.ResumableStaged,
            TriggerStrategy.Fire,
            ConstraintStrategy.Enforce,
            [new StableKeyDefinition(Children, "PK_verify_children", ["id"])],
            [
                new PlanTable(
                    new TableMapping(Children, Children, [new("id", "id"), new("parent_id", "parent_id")]),
                    PlanTableState.Root,
                    new ManifestCounts(plannedChildren, plannedChildren, plannedChildren, 0),
                    new TopologicalGroup([Children]),
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
