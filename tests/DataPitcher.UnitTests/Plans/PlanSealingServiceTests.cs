using System.Text.Json;
using DataPitcher.Application.Plans;
using DataPitcher.ControlStore;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.UnitTests.Closure;
using DataPitcher.UnitTests.Infrastructure;
using Xunit;

namespace DataPitcher.UnitTests.Plans;

/// <summary>
/// Drives <see cref="PlanSealingService"/> end to end over the real control stores with a fake provider whose
/// closure runs in memory, so every refusal and warning sealing can produce is exercised without a database.
/// </summary>
public sealed class PlanSealingServiceTests
{
    private static readonly TableDefinition Child = Table("C", ("K1", false), ("PId", false));
    private static readonly TableDefinition Parent = Table("P", ("K1", false), ("CId", true), ("GId", true));
    private static readonly TableDefinition Grandparent = Table("G", ("K1", false));
    private static readonly ForeignKeyDefinition ParentToGrandparent = new(
        "FK_P_G",
        Parent,
        Grandparent,
        ["GId"],
        ["K1"],
        true,
        true
    );
    private static readonly ForeignKeyDefinition ChildToParent = new(
        "FK_C_P",
        Child,
        Parent,
        ["PId"],
        ["K1"],
        true,
        true
    );

    [Fact]
    public async Task SealAsync_StampsTheSealingVersionAndCarriesTheClosureWarnings()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent, Grandparent], [ChildToParent, ParentToGrandparent]);
        var pg = new ClosureRelationship(ParentToGrandparent);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Store.Link(pg, (K(2), K(3)));
        // The parent exists in the target but its own constraint is untrusted: it is transferred, and the plan says why.
        session.Store.MarkTarget(Parent, K(2));
        session.Store.SetTargetConstraint(pg, new TargetConstraintState("FK_P_G", true, true, false));
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        Assert.Equal(TransferPlanContent.CurrentSealingVersion, content.SealingVersion);
        Assert.True(content.IsSealedByCurrentVersion);
        Assert.Equal(VerificationStrategy.StrictExact, content.VerificationStrategy);
        Assert.Equal(
            ["dbo.G", "dbo.P", "dbo.C"],
            content.Tables.Select(table => $"{table.Mapping.Source.Schema}.{table.Mapping.Source.Name}")
        );
        Assert.Equal(["target_constraint_untrusted"], content.Warnings.Select(warning => warning.Code));
        Assert.Contains("FK_P_G", content.Warnings[0].Message);
    }

    [Fact]
    public async Task SealAsync_WhenTheTargetCannotRecordExactWrites_DowngradesVerificationAndSaysWhy()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.VerificationBlockers = ["StrictExact is blocked by a target trigger on dbo.C."];
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        Assert.Equal(VerificationStrategy.Standard, content.VerificationStrategy);
        var warning = Assert.Single(content.Warnings);
        Assert.Equal("verification_downgraded", warning.Code);
        Assert.StartsWith("StrictExact is blocked by a target trigger on dbo.C.", warning.Message);
        Assert.Equal(["dbo.C", "dbo.P"], session.VerifiedTables);
    }

    [Fact]
    public async Task SealAsync_WhenAPlannedTableReferencesATableTheLoginCannotSee_Refuses()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Unresolved =
        [
            new UnresolvedForeignKey(
                "FK_P_Hidden",
                new SchemaTableAddress("dbo", "P"),
                new SchemaTableAddress("vault", "Hidden")
            ),
        ];
        var (service, _, planId) = await ArrangeAsync(fixture, session);

        var exception = await Assert.ThrowsAsync<IncompleteGraphException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );

        Assert.Contains(
            "FK_P_Hidden on dbo.P references vault.Hidden, which the source login cannot read",
            exception.Message
        );
        Assert.Contains("Grant the source login SELECT", exception.Message);
    }

    [Fact]
    public async Task SealAsync_WhenAnUnresolvedForeignKeyBelongsToATableOutsideThePlan_Seals()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Unresolved =
        [
            new UnresolvedForeignKey(
                "FK_Other_Hidden",
                new SchemaTableAddress("dbo", "Other"),
                new SchemaTableAddress("vault", "Hidden")
            ),
        ];
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        Assert.NotNull(await plans.LoadContentAsync(planId, CancellationToken.None));
    }

    [Fact]
    public async Task SealAsync_WhenSourceRowsAreOrphanedAndTheTargetEnforcesTheConstraint_Refuses()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent], targetEnforces: true);
        var cp = new ClosureRelationship(ChildToParent);
        session.Store.Link(cp, (K(1), K(2)));
        session.Store.SetOrphans(cp, 2);
        var (service, _, planId) = await ArrangeAsync(fixture, session);

        var exception = await Assert.ThrowsAsync<SourceOrphansException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );

        Assert.Contains(
            "2 planned row(s) in dbo.C reference a dbo.P row through FK_C_P that does not exist in the source",
            exception.Message
        );
        Assert.Contains("The target enforces the constraint", exception.Message);
    }

    [Fact]
    public async Task SealAsync_WhenSourceRowsAreOrphanedAndTheTargetDoesNotEnforceTheConstraint_Warns()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent], targetEnforces: false);
        var cp = new ClosureRelationship(ChildToParent);
        session.Store.Link(cp, (K(1), K(2)));
        session.Store.SetOrphans(cp, 1);
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        var warning = Assert.Single(content.Warnings);
        Assert.Equal("source_orphans", warning.Code);
        Assert.Contains("the target does not enforce the constraint", warning.Message);
    }

    [Fact]
    public async Task SealAsync_WhenTheTargetSnapshotLacksAPlannedTable_JudgesNullabilityFromTheSource()
    {
        using var fixture = new ControlDatabaseFixture();
        var parentToChild = new ForeignKeyDefinition("FK_P_C", Parent, Child, ["CId"], ["K1"], true, true);
        var session = Session([Child, Parent], [ChildToParent, parentToChild]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Store.Link(new ClosureRelationship(parentToChild), (K(2), K(1)));
        // The target scan knows neither table (a target business schema that differs from the source's).
        session.TargetSchema = new SchemaSnapshotContent([], []);
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        var parent = Assert.Single(content.Tables, table => table.Mapping.Source.Name == "P");
        Assert.Equal(["CId"], parent.DeferredColumns);
    }

    [Fact]
    public async Task SealAsync_WhenTablesFormACycleWithoutANullableColumn_Refuses()
    {
        using var fixture = new ControlDatabaseFixture();
        var strictParent = Table("P", ("K1", false), ("CId", false));
        var childToParent = new ForeignKeyDefinition("FK_C_P", Child, strictParent, ["PId"], ["K1"], true, true);
        var parentToChild = new ForeignKeyDefinition("FK_P_C", strictParent, Child, ["CId"], ["K1"], true, true);
        var session = Session([Child, strictParent], [childToParent, parentToChild]);
        session.Store.Link(new ClosureRelationship(childToParent), (K(1), K(2)));
        session.Store.Link(new ClosureRelationship(parentToChild), (K(2), K(1)));
        var (service, _, planId) = await ArrangeAsync(fixture, session);

        var exception = await Assert.ThrowsAsync<UnorderablePlanException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );

        Assert.Contains("FK_C_P", exception.Message);
        Assert.Contains("FK_P_C", exception.Message);
    }

    [Fact]
    public async Task SealAsync_WhenACycleHasANullableColumn_DefersItOnThePlanTable()
    {
        using var fixture = new ControlDatabaseFixture();
        var parentToChild = new ForeignKeyDefinition("FK_P_C", Parent, Child, ["CId"], ["K1"], true, true);
        var session = Session([Child, Parent], [ChildToParent, parentToChild]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Store.Link(new ClosureRelationship(parentToChild), (K(2), K(1)));
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        var parent = Assert.Single(content.Tables, table => table.Mapping.Source.Name == "P");
        Assert.Equal(["CId"], parent.DeferredColumns);
        Assert.Equal(CycleStrategy.NullableForeignKeyTwoPhase, parent.CycleStrategy);
        Assert.Equal(
            ["dbo.P", "dbo.C"],
            content.Tables.Select(table => $"{table.Mapping.Source.Schema}.{table.Mapping.Source.Name}")
        );
        Assert.Empty(content.Warnings);
    }

    [Fact]
    public async Task SealAsync_WhileATransferOfThePlanIsActive_RefusesUntilThatJobHasEnded()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        var (service, plans, planId) = await ArrangeAsync(fixture, session);
        await service.SealAsync(planId, CancellationToken.None);
        var store = new JobStore(fixture.Database, fixture.Clock);
        var job = store.Start(new StartJobRequest(planId, "run-1")).Job;

        var queued = await Assert.ThrowsAsync<PlanInUseException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );
        var claim = (await store.TryClaimNextAsync("worker-a", TimeSpan.FromMinutes(1), CancellationToken.None))!;
        await store.PrepareAsync(claim, CancellationToken.None);
        await store.MarkRunningAsync(claim.Lease, CancellationToken.None);
        var running = await Assert.ThrowsAsync<PlanInUseException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );
        await store.MarkFailedAsync(claim.Lease, "transfer_failed", "gone", CancellationToken.None);
        await service.SealAsync(planId, CancellationToken.None);

        Assert.Contains($"Transfer job {job.JobId} for this plan is Queued", queued.Message);
        Assert.Contains($"Transfer job {job.JobId} for this plan is Running", running.Message);
        Assert.Contains("wait for it to finish or cancel it", running.Message);
        Assert.Equal(1, session.Store.ResetCalls - 1);
        Assert.NotNull(await plans.LoadContentAsync(planId, CancellationToken.None));
    }

    [Fact]
    public async Task SealAsync_WhenPlannedRowsCollideWithDifferentTargetRowsOnAUniqueKey_Refuses()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Collisions =
        [
            new UniqueKeyCollision(new TableAddress("dbo", "P"), ["Code"], 2, ["K1=2 (source) -> K1=9 (target)"]),
        ];
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        var exception = await Assert.ThrowsAsync<UniqueKeyCollisionException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );

        Assert.Contains(
            "2 row(s) of dbo.P on unique key (Code), for example K1=2 (source) -> K1=9 (target)",
            exception.Message
        );
        Assert.Contains("cannot merge two rows into one", exception.Message);
        Assert.Null(await plans.LoadContentAsync(planId, CancellationToken.None));
    }

    [Fact]
    public async Task SealAsync_WhenAPlanIsSealedAgain_StartsFromAnEmptyStagedSetAndFindsTheSameRows()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);
        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        Assert.Equal(2, session.Store.ResetCalls);
        Assert.Equal(2, content.ManifestTotals.PlannedWrites);
        Assert.Empty(content.Warnings);
    }

    [Fact]
    public async Task SealAsync_WhenEverySelectedRowAlreadyExistsInTheTarget_SealsAnEmptyPlanAndSaysWhy()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.Store.Link(new ClosureRelationship(ChildToParent), (K(1), K(2)));
        session.Store.MarkTarget(Child, K(1));
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        Assert.Empty(content.Tables);
        Assert.Equal(0, content.ManifestTotals.PlannedWrites);
        var warning = Assert.Single(content.Warnings);
        Assert.Equal("roots_skipped", warning.Code);
        Assert.StartsWith("1 of 1 selected row(s) already exist in the target", warning.Message);
        Assert.Contains("for example K1=1", warning.Message);
        Assert.Contains("Nothing is left to transfer", warning.Message);
    }

    [Fact]
    public async Task SealAsync_WhenTheSelectionReturnsNoRows_SealsAnEmptyPlanAndSaysWhy()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        session.RootKeys = [];
        var (service, plans, planId) = await ArrangeAsync(fixture, session);

        await service.SealAsync(planId, CancellationToken.None);

        var content = (await plans.LoadContentAsync(planId, CancellationToken.None))!;
        Assert.Empty(content.Tables);
        Assert.Equal("selection_empty", Assert.Single(content.Warnings).Code);
    }

    [Fact]
    public async Task SealAsync_WhenTheSourceSchemaChangedSinceTheSelectionsSnapshot_RefusesAndSaysWhereToRepointIt()
    {
        using var fixture = new ControlDatabaseFixture();
        var session = Session([Child, Parent], [ChildToParent]);
        // Even with a newer scan of the current schema stored, the selection's own snapshot is what counts.
        var (service, _, planId) = await ArrangeAsync(
            fixture,
            session,
            selectionSnapshot: new SchemaSnapshotContent([], []),
            currentScanExists: true
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SealAsync(planId, CancellationToken.None)
        );

        Assert.Contains("2026-09-02", exception.Message);
        Assert.Contains("choose the current snapshot under \"Schema snapshot\"", exception.Message);
        Assert.Contains("save the selection, and seal the plan again", exception.Message);
    }

    private static async Task<(PlanSealingService Service, PlanStore Plans, Guid PlanId)> ArrangeAsync(
        ControlDatabaseFixture fixture,
        FakeSealingSession session,
        SchemaSnapshotContent? selectionSnapshot = null,
        bool currentScanExists = false
    )
    {
        fixture.Migrator.Apply();
        var profiles = new ConnectionProfileStore(fixture.Database, fixture.Clock);
        var source = await profiles.CreateAsync(Draft("source"), "source", CancellationToken.None);
        var target = await profiles.CreateAsync(Draft("target"), "target", CancellationToken.None);
        var snapshotId = InsertSnapshot(fixture, source.ConnectionId, selectionSnapshot ?? session.SourceSchema);
        if (currentScanExists)
            InsertSnapshot(fixture, source.ConnectionId, session.SourceSchema);
        var selections = new SelectionStore(fixture.Database, fixture.Clock);
        var selectionId = Guid.NewGuid();
        await selections.SaveAsync(
            selectionId,
            "roots",
            """{"Mode":"raw","RawSql":"SELECT K1 AS [__datapitcher_key_0] FROM dbo.C","Parameters":[]}""",
            "\"0\"",
            CancellationToken.None,
            source.ConnectionId,
            snapshotId,
            "dbo",
            "C",
            "PK_C",
            ["K1"]
        );
        var plans = new PlanStore(fixture.Database, fixture.Clock);
        var planId = Guid.NewGuid();
        await plans.SaveAsync(
            planId,
            "plan",
            null,
            "\"0\"",
            CancellationToken.None,
            selectionId,
            source.ConnectionId,
            target.ConnectionId
        );
        var service = new PlanSealingService(
            plans,
            selections,
            profiles,
            new SchemaSnapshotStore(fixture.Database, fixture.Clock),
            new Resolver(),
            [new FakeSealingProvider(session)],
            new JobStore(fixture.Database, fixture.Clock)
        );
        return (service, plans, planId);
    }

    private static Guid InsertSnapshot(ControlDatabaseFixture fixture, Guid connectionId, SchemaSnapshotContent content)
    {
        var snapshotId = Guid.NewGuid();
        using var db = fixture.Database.Open();
        db.Execute(
            "INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)",
            new ControlParameter("snapshotId", snapshotId.ToString()),
            new ControlParameter("connectionId", connectionId.ToString()),
            new ControlParameter("snapshotHash", CanonicalSchemaSnapshotHasher.Hash(content)),
            new ControlParameter("contentJson", JsonSerializer.Serialize(content)),
            new ControlParameter("createdUtc", fixture.Clock.UtcNow.ToString("O"))
        );
        return snapshotId;
    }

    private static ConnectionProfileDraft Draft(string name) =>
        new(name, "fake", new SecretReference(SecretReferenceKind.EnvironmentVariable, name), "dbo", "dbo");

    private static FakeSealingSession Session(
        TableDefinition[] tables,
        ForeignKeyDefinition[] foreignKeys,
        bool targetEnforces = true
    )
    {
        var schema = new SchemaSnapshotContent(
            tables.Select(table => new SchemaTable(
                table.Schema,
                table.Name,
                table.Columns.Select(column => new SchemaColumn(column.Name, "int", "System.Int32", column.IsNullable)),
                new SchemaKey(table.PrimaryKey!.Name, table.PrimaryKey.Columns),
                []
            )),
            foreignKeys.Select(foreignKey => new SchemaForeignKey(
                foreignKey.Name,
                new SchemaTableAddress(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                new SchemaTableAddress(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name),
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                targetEnforces,
                targetEnforces
            ))
        );
        return new FakeSealingSession(schema, tables, foreignKeys, [K(1)]);
    }

    private static TableDefinition Table(string name, params (string Name, bool Nullable)[] columns) =>
        new(
            "dbo",
            name,
            columns.Select(column => new ColumnDefinition(column.Name, typeof(int), column.Nullable)).ToArray(),
            new UniqueConstraint("PK_" + name, ["K1"]),
            []
        );

    private static StableKey K(int value) => new([new KeyComponent("K1", value)]);

    private sealed class Resolver : ISecretReferenceResolver
    {
        public Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult("Server=fake");
    }

    private sealed class FakeSealingProvider(FakeSealingSession session) : ISealingProvider
    {
        public string ProviderId => "fake";

        public Task<ISealingSession> OpenAsync(
            ConnectionProfile source,
            string sourceConnectionString,
            ConnectionProfile target,
            string targetConnectionString,
            CancellationToken cancellationToken
        ) => Task.FromResult<ISealingSession>(session);
    }

    private sealed class FakeSealingSession(
        SchemaSnapshotContent schema,
        IReadOnlyCollection<TableDefinition> tables,
        IReadOnlyCollection<ForeignKeyDefinition> foreignKeys,
        IReadOnlyList<StableKey> rootKeys
    ) : ISealingSession
    {
        public InMemoryClosureStore Store { get; } = new();
        public IReadOnlyList<StableKey> RootKeys { get; set; } = rootKeys;
        public IReadOnlyCollection<UnresolvedForeignKey> Unresolved { get; set; } = [];
        public IReadOnlyCollection<string> VerificationBlockers { get; set; } = [];
        public IReadOnlyCollection<UniqueKeyCollision> Collisions { get; set; } = [];

        public Task<IReadOnlyCollection<UniqueKeyCollision>> FindUniqueKeyCollisionsAsync(
            IReadOnlyCollection<TableDefinition> planned,
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId,
            CancellationToken cancellationToken
        ) => Task.FromResult(Collisions);

        public List<string> VerifiedTables { get; } = [];
        public SchemaSnapshotContent SourceSchema { get; } = schema;
        public SchemaSnapshotContent TargetSchema { get; set; } = schema;
        public IReadOnlyCollection<TableDefinition> SourceTables => tables;
        public IReadOnlyCollection<ForeignKeyDefinition> SourceForeignKeys => foreignKeys;
        public IReadOnlyCollection<UnresolvedForeignKey> SourceUnresolvedForeignKeys => Unresolved;

        public Task<IReadOnlyCollection<string>> VerificationBlockersAsync(
            IReadOnlyCollection<TableAddress> addresses,
            CancellationToken cancellationToken
        )
        {
            VerifiedTables.AddRange(
                addresses
                    .Select(address => address.Schema + "." + address.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
            );
            return Task.FromResult(VerificationBlockers);
        }

        public Task ValidateAsync(GeneratedSelectionSql selection, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SelectionKeySet> ReadKeysAsync(
            GeneratedSelectionSql selection,
            int maximumResultSize,
            CancellationToken cancellationToken
        ) => Task.FromResult(new SelectionKeySet(selection.RootTable, RootKeys));

        public IClosureStore CreateClosureStore(
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId
        ) => Store;

        public Task OrderHierarchiesAsync(
            IReadOnlyCollection<ClosureRelationship> selfRelationships,
            IReadOnlyDictionary<TableDefinition, StableKeySelection> stableKeys,
            Guid planId,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
