using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Closure;

public sealed class DependencyClosureTests
{
    [Fact]
    public async Task Closure_WhenChildSelected_IncludesMissingParent()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.Link(e, (K(1), K(2)));
        var r = await Run(s, [e], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenParentSelected_ExcludesInboundChildrenByDefault()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.Link(e, (K(1), K(2)));
        s.LinkReverse(e, (K(2), K(1)));
        var r = await Run(s, [e], Root(p, 2));
        Assert.False(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenInboundRelationshipEnabled_IncludesChildren()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p), true);
        var s = new InMemoryClosureStore();
        s.Link(e, (K(2), K(1)));
        var r = await Run(s, [e], Root(p, 2));
        Assert.True(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenOptionalForeignKeyIsNull_AddsNoParent()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p, ["Ref"], ["Code"]));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["Ref"] = null });
        s.AddRow(p, K(2), new Dictionary<string, object?> { ["Code"] = 2 });
        var r = await Run(s, [e], Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyIsComposite_ResolvesParentByConstraintNativePosition()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p, ["ChildFirst", "ChildSecond"], ["ParentFirst", "ParentSecond"]));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["ChildFirst"] = 7, ["ChildSecond"] = 8 });
        s.AddRow(p, K(9), new Dictionary<string, object?> { ["ParentFirst"] = 7, ["ParentSecond"] = 8 });
        var r = await Run(s, [e], Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyReferencesUniqueConstraint_ResolvesByThatKeyRatherThanPrimaryKey()
    {
        var c = T("C");
        var p = T("P", ["Code"]);
        var e = E(F(c, p, ["RefCode"], ["Code"]));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["RefCode"] = "external" });
        s.AddRow(p, K(9), new Dictionary<string, object?> { ["Code"] = "external" });
        var r = await Run(s, [e], Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
    }

    [Fact]
    public async Task Closure_WhenTwoForeignKeysBetweenSameTables_AppliesBothRelationships()
    {
        var c = T("C");
        var p = T("P");
        var billTo = E(F(c, p, ["BillCode"], ["Code"], name: "FK_BillTo"));
        var shipTo = E(F(c, p, ["ShipCode"], ["Code"], name: "FK_ShipTo"));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["BillCode"] = "B", ["ShipCode"] = "S" });
        s.AddRow(p, K(9), new Dictionary<string, object?> { ["Code"] = "B" });
        s.AddRow(p, K(10), new Dictionary<string, object?> { ["Code"] = "S" });
        var r = await Run(s, [billTo, shipTo], Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
        Assert.True(r.Contains(p, K(10)));
    }

    [Fact]
    public async Task Closure_WhenRelationshipIsManuallyDeclared_ExpandsItLikeAForeignKey()
    {
        var c = T("C");
        var p = T("P");
        var e = ClosureRelationship.Manual("Manual_C_P", c, p, ["K1"], ["K1"]);
        var s = new InMemoryClosureStore();
        s.Link(e, (K(1), K(2)));
        var r = await Run(s, [e], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenSourceForeignKeyIsOrphaned_TransfersChildWithoutFabricatingParent()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p, ["Ref"], ["Code"]));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["Ref"] = "missing" });
        var r = await Run(s, [e], Root(c, 1));
        Assert.True(r.Contains(c, K(1)));
        Assert.False(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenParentSharedByTwoChildren_IncludesParentOnce()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.Link(e, (K(1), K(9)), (K(2), K(9)));
        var r = await Run(s, [e], new ClosureRoot(c, [K(1), K(2)], RootConflictPolicy.FailOnConflict));
        Assert.Single(r.Rows, x => x.Table == p && x.Key == K(9));
    }

    [Fact]
    public async Task Closure_WhenRootIsFailOnConflictAndExists_Throws()
    {
        var c = T("C");
        var s = new InMemoryClosureStore();
        s.MarkTarget(c, K(1));
        await Assert.ThrowsAsync<RootConflictException>(() => Run(s, [], Root(c, 1)));
    }

    [Fact]
    public async Task Closure_WhenRootIsSkipExistingAndExists_ExpandsNothing()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.MarkTarget(c, K(1));
        s.Link(e, (K(1), K(2)));
        var r = await Run(s, [e], Root(c, 1, RootConflictPolicy.SkipExisting));
        Assert.False(r.Contains(c, K(1)));
        Assert.False(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenRootIsUpsertAndExists_ExpandsDependencies()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.MarkTarget(c, K(1));
        s.Link(e, (K(1), K(2)));
        var r = await Run(s, [e], Root(c, 1, RootConflictPolicy.Upsert));
        Assert.True(r.Contains(c, K(1)));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsBehindTrustedTargetConstraint_TerminatesItsAncestorBranch()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.False(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentDoesNotExistDespiteTrustedTargetConstraint_TransfersIt()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.SetTargetConstraint(pg, Target(pg));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsButItsTargetConstraintIsAbsent_TransfersParentAnyway()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(cp, Target(cp));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsUntrusted_TransfersParentAndNamesTheConstraint()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg, isTrusted: false));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
        Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"), r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsDisabled_TransfersParentAnyway()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg, isEnforced: false));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenSourceConstraintMetadataDisagrees_UsesTrustedTargetConstraint()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g, isEnforced: false, isTrusted: false));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.False(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenRelationshipDisabled_ContributesNoRows()
    {
        var r = T("R");
        var p = T("P");
        var x = T("X");
        var rp = E(F(r, p));
        var px = new ClosureRelationship(F(p, x), false, false);
        var s = new InMemoryClosureStore();
        s.Link(rp, (K(1), K(2)));
        s.Link(px, (K(2), K(3)));
        var result = await Run(s, [rp, px], Root(r, 1));
        Assert.True(result.Contains(p, K(2)));
        Assert.False(result.Contains(x, K(3)));
    }

    [Fact]
    public async Task Closure_WhenGraphHasTwoTableCycle_Terminates()
    {
        var a = T("A");
        var b = T("B");
        var ab = E(F(a, b));
        var ba = E(F(b, a));
        var s = new InMemoryClosureStore();
        s.Link(ab, (K(1), K(2)));
        s.Link(ba, (K(2), K(1)));
        var r = await Run(s, [ab, ba], Root(a, 1));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenTableIsSelfReferencing_FollowsHierarchyAndTerminates()
    {
        var eTable = T("E");
        var e = E(F(eTable, eTable));
        var s = new InMemoryClosureStore();
        s.Link(e, (K(2), K(1)), (K(1), K(1)));
        var r = await Run(s, [e], Root(eTable, 2));
        Assert.True(r.Contains(eTable, K(1)));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenChainExpands_StampsBreadthFirstGenerations()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.Equal(0, Assert.Single(r.Rows, row => row.Table == c).Generation);
        Assert.Equal(1, Assert.Single(r.Rows, row => row.Table == p).Generation);
        Assert.Equal(2, Assert.Single(r.Rows, row => row.Table == g).Generation);
    }

    [Fact]
    public async Task Closure_WhenEnabledParticipantIsBlocked_RejectsRequestBeforeSeeding()
    {
        var c = T("C");
        var blocked = new TableDefinition("dbo", "Blocked", [], null, []);
        var e = E(F(c, blocked));
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [e],
            new Dictionary<TableDefinition, StableKeySelection>
            {
                [c] = StableKeySelector.Select(c, null),
                [blocked] = StableKeySelection.NoStableKey,
            }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(s).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, s.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenParticipantHasNoStableKeySelection_RejectsRequestBeforeSeeding()
    {
        var c = T("C");
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest([Root(c, 1)], [], new Dictionary<TableDefinition, StableKeySelection>());
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(s).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, s.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenRootUsesExplicitNonNullableUniqueStableKey_IncludesIt()
    {
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        var c = new TableDefinition("dbo", "C", [new ColumnDefinition("Code", typeof(string), false)], null, [unique]);
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = StableKeySelector.Select(c, "UQ_Code") }
        );
        var r = await new DependencyClosure(s).ComputeAsync(request, CancellationToken.None);
        Assert.True(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenSelectedUniqueContainsNullableColumn_RejectsRequestBeforeSeeding()
    {
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        var c = new TableDefinition("dbo", "C", [new ColumnDefinition("Code", typeof(string), true)], null, [unique]);
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = StableKeySelector.Select(c, "UQ_Code") }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(s).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, s.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenSelectionDirectlyCarriesNonPrimaryKeyConstraintWithNullableColumn_RejectsRequestBeforeSeeding()
    {
        var nonPrimaryConstraint = new UniqueConstraint("UQ_Other", ["Code"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), true)],
            null,
            [nonPrimaryConstraint]
        );
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = new StableKeySelection(nonPrimaryConstraint) }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(s).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, s.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenSelectedConstraintReferencesColumnAbsentFromTable_RejectsRequestBeforeSeeding()
    {
        var constraint = new UniqueConstraint("UQ_Missing", ["MissingColumn"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), false)],
            null,
            [constraint]
        );
        var s = new InMemoryClosureStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = new StableKeySelection(constraint) }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(s).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, s.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor()
    {
        var r = T("R");
        var a = T("A");
        var b = T("B");
        var x = T("X");
        var ra = E(F(r, a));
        var rb = E(F(r, b));
        var ax = E(F(a, x));
        var bx = E(F(b, x));
        var s = new InMemoryClosureStore();
        s.MarkTarget(a, K(2));
        s.SetTargetConstraint(ax, Target(ax));
        s.Link(ra, (K(1), K(2)));
        s.Link(rb, (K(1), K(3)));
        s.Link(ax, (K(2), K(4)));
        s.Link(bx, (K(3), K(4)));
        var result = await Run(s, [ra, rb, ax, bx], Root(r, 1));
        Assert.True(result.Contains(x, K(4)));
    }

    [Fact]
    public async Task Closure_WhenAncestorDemandedByTwoIncludedPaths_AppearsExactlyOnce()
    {
        var r = T("R");
        var a = T("A");
        var b = T("B");
        var x = T("X");
        var ra = E(F(r, a));
        var rb = E(F(r, b));
        var ax = E(F(a, x));
        var bx = E(F(b, x));
        var s = new InMemoryClosureStore();
        s.Link(ra, (K(1), K(2)));
        s.Link(rb, (K(1), K(3)));
        s.Link(ax, (K(2), K(4)));
        s.Link(bx, (K(3), K(4)));
        var result = await Run(s, [ra, rb, ax, bx], Root(r, 1));
        Assert.Single(result.Rows, row => row.Table == x && row.Key == K(4));
    }

    [Fact]
    public void StableKey_WhenReconstructedWithSameComponents_IsFoundInClosureResult()
    {
        var table = T("T");
        var result = new ClosureResult([new ClosureRow(table, K(1), 0)], []);
        Assert.True(result.Contains(table, K(1)));
    }

    [Fact]
    public async Task Closure_WhenTwoRootsSelectTheSameKey_IncludesItOnce()
    {
        var c = T("C");
        var s = new InMemoryClosureStore();
        var r = await Run(
            s,
            [],
            new ClosureRoot(c, [K(1)], RootConflictPolicy.FailOnConflict),
            new ClosureRoot(c, [K(1)], RootConflictPolicy.FailOnConflict)
        );
        Assert.Single(r.Rows, row => row.Table == c && row.Key == K(1));
    }

    [Fact]
    public async Task Closure_WhenTwoRootsClaimSameKeyWithDifferentPolicies_IsRejected()
    {
        var c = T("C");
        foreach (
            var roots in new[]
            {
                new[] { Root(c, 1), Root(c, 1, RootConflictPolicy.SkipExisting) },
                new[] { Root(c, 1, RootConflictPolicy.SkipExisting), Root(c, 1) },
            }
        )
        {
            var s = new InMemoryClosureStore();
            s.MarkTarget(c, K(1));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Run(s, [], roots));
        }
    }

    [Fact]
    public async Task Closure_WhenRootsAreSuppliedInAnyOrder_ProducesIdenticalResult()
    {
        var c = T("C");
        var p = T("P");
        var e = E(F(c, p));
        var firstStore = new InMemoryClosureStore();
        firstStore.MarkTarget(c, K(1));
        firstStore.Link(e, (K(1), K(2)));
        var secondStore = new InMemoryClosureStore();
        secondStore.MarkTarget(c, K(1));
        secondStore.Link(e, (K(1), K(2)));
        var first = await Outcome(
            Run(firstStore, [e], Root(c, 1, RootConflictPolicy.Upsert), Root(c, 1, RootConflictPolicy.SkipExisting))
        );
        var second = await Outcome(
            Run(secondStore, [e], Root(c, 1, RootConflictPolicy.SkipExisting), Root(c, 1, RootConflictPolicy.Upsert))
        );
        Assert.Equal(first.Error?.GetType(), second.Error?.GetType());
        Assert.Equal(first.Error?.Message, second.Error?.Message);
        Assert.Equal(first.Result?.Rows.ToArray(), second.Result?.Rows.ToArray());
        Assert.Equal(first.Result?.Warnings.ToArray(), second.Result?.Warnings.ToArray());
    }

    [Fact]
    public async Task Closure_WhenProbeReturnsEquivalentRelationshipInstance_StillResolvesConstraintState()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var equivalent = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetProbeConstraint(pg, equivalent, Target(equivalent));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenOneOfTwoConstraintsIsUntrusted_TransfersTheRow()
    {
        var c = T("C");
        var p = T("P");
        var g1 = T("G1");
        var g2 = T("G2");
        var cp = E(F(c, p));
        var pg1 = E(F(p, g1));
        var pg2 = E(F(p, g2));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg1, Target(pg1));
        s.SetTargetConstraint(pg2, Target(pg2, isTrusted: false));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg1, (K(2), K(3)));
        s.Link(pg2, (K(2), K(4)));
        var r = await Run(s, [cp, pg1, pg2], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenTargetConstraintIsNotPresent_TransfersRowAnyway()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg, isPresent: false));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenRowIsGenuinelySatisfied_RecordsNoWarning()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenProbeOmitsRelationshipEntry_TransfersRowAndWarns()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.OmitProbeConstraint(pg);
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.Contains(new TargetConstraintWarning(pg.Name), r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenTargetConstraintIsUnenforced_RecordsWarningNamingTheConstraint()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg, isEnforced: false));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"), r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenCancellationIsRequested_StopsPromptly()
    {
        var table = T("T");
        var relationship = ClosureRelationship.Manual("T_T", table, table, ["K1"], ["K1"]);
        var s = new InMemoryClosureStore();
        for (var key = 1; key <= 50000; key++)
            s.Link(relationship, (K(key), K(key + 1)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new ClosureRequest(
            [Root(table, 1)],
            [relationship],
            new Dictionary<TableDefinition, StableKeySelection> { [table] = StableKeySelector.Select(table, null) }
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DependencyClosure(s).ComputeAsync(request, cancellation.Token)
        );
    }

    [Fact]
    public async Task Closure_CountsTheSkipExistingRootsItLeftOut()
    {
        var c = T("C");
        var s = new InMemoryClosureStore();
        s.MarkTarget(c, K(1));
        var r = await Run(
            s,
            [],
            new ClosureRoot(c, [K(1), K(2)], RootConflictPolicy.SkipExisting),
            Root(c, 3, RootConflictPolicy.Upsert)
        );
        Assert.Equal(1, r.SkippedRoots);
        var sample = Assert.Single(r.SkippedRootSamples);
        Assert.Equal(K(1), sample.Source);
        Assert.Equal(K(1), sample.Target);
        Assert.True(r.Contains(c, K(2)));
        Assert.True(r.Contains(c, K(3)));
    }

    [Fact]
    public async Task Closure_KeepsAtMostThreeSamplesOfTheSkipExistingRootsItLeftOut()
    {
        var c = T("C");
        var s = new InMemoryClosureStore();
        s.MarkTarget(c, K(1), K(2), K(3), K(4), K(5));
        var r = await Run(s, [], new ClosureRoot(c, [K(1), K(2), K(3), K(4), K(5)], RootConflictPolicy.SkipExisting));
        Assert.Equal(5, r.SkippedRoots);
        Assert.Equal(3, r.SkippedRootSamples.Count);
        Assert.All(r.SkippedRootSamples, sample => Assert.Equal(sample.Source, sample.Target));
    }

    [Fact]
    public async Task Closure_WhenSourceRowsPointAtMissingParents_ReportsTheOrphansPerRelationship()
    {
        var c = T("C");
        var p = T("P");
        var cp = E(F(c, p));
        var s = new InMemoryClosureStore();
        s.Link(cp, (K(1), K(2)));
        s.SetOrphans(cp, 3);
        var r = await Run(s, [cp], Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.Equal([new SourceOrphanWarning(cp.Name, 3)], r.Orphans);
    }

    [Fact]
    public async Task Closure_WhenARelationshipIsExpandedInSeveralGenerations_SumsItsOrphans()
    {
        var n = T("N");
        var self = E(F(n, n));
        var s = new InMemoryClosureStore();
        s.Link(self, (K(1), K(2)), (K(2), K(3)));
        s.SetOrphans(self, 1);
        var r = await Run(s, [self], Root(n, 1));
        Assert.Equal([new SourceOrphanWarning(self.Name, 3)], r.Orphans);
    }

    [Fact]
    public async Task Closure_WhenAChildRowCarriesAForeignKeyValueWithoutAParentRow_CountsItAsAnOrphan()
    {
        var c = T("C");
        var p = T("P");
        var cp = E(F(c, p, ["PId"], ["K1"]));
        var s = new InMemoryClosureStore();
        s.AddRow(c, K(1), new Dictionary<string, object?> { ["PId"] = 9 });
        s.AddRow(c, K(2), new Dictionary<string, object?> { ["PId"] = null });
        s.AddRow(p, K(3), new Dictionary<string, object?> { ["K1"] = 3 });
        var r = await Run(s, [cp], new ClosureRoot(c, [K(1), K(2)], RootConflictPolicy.FailOnConflict));
        Assert.Equal([new SourceOrphanWarning(cp.Name, 1)], r.Orphans);
        Assert.False(r.Contains(p, K(3)));
    }

    [Fact]
    public async Task Closure_MarksIncludedKeysInTheStoreAndLeavesSatisfiedParentsUnmarked()
    {
        var c = T("C");
        var p = T("P");
        var g = T("G");
        var cp = E(F(c, p));
        var pg = E(F(p, g));
        var s = new InMemoryClosureStore();
        s.MarkTarget(p, K(2));
        s.SetTargetConstraint(pg, Target(pg));
        s.Link(cp, (K(1), K(2)));
        s.Link(pg, (K(2), K(3)));
        var r = await Run(s, [cp, pg], Root(c, 1));
        Assert.True(r.Contains(c, K(1)));
        Assert.Equal([K(1)], s.Included(c));
        Assert.Empty(s.Included(p));
        Assert.Empty(s.Included(g));
        Assert.Empty(r.Orphans);
    }

    private static TableDefinition T(string name, string[]? uniqueColumns = null) =>
        new(
            "dbo",
            name,
            [],
            new UniqueConstraint($"PK_{name}", ["K1"]),
            uniqueColumns is null ? [] : [new UniqueConstraint($"UQ_{name}", uniqueColumns)]
        );

    private static StableKey K(params object?[] values) =>
        new(values.Select((value, index) => new KeyComponent($"K{index + 1}", value)));

    private static ForeignKeyDefinition F(
        TableDefinition child,
        TableDefinition parent,
        string[]? childColumns = null,
        string[]? parentColumns = null,
        bool isEnforced = true,
        bool isTrusted = true,
        string? name = null
    )
    {
        var childColumnsToUse = childColumns ?? ["K1"];
        var parentColumnsToUse = parentColumns ?? ["K1"];
        return new ForeignKeyDefinition(
            name ?? $"FK_{child.Name}_{parent.Name}",
            child,
            parent,
            childColumnsToUse,
            parentColumnsToUse,
            isEnforced,
            isTrusted
        );
    }

    private static ClosureRelationship E(ForeignKeyDefinition foreignKey, bool inbound = false) =>
        new(foreignKey, inbound);

    private static ClosureRoot Root(
        TableDefinition table,
        int key,
        RootConflictPolicy policy = RootConflictPolicy.FailOnConflict
    ) => new(table, [K(key)], policy);

    private static TargetConstraintState Target(
        ClosureRelationship relationship,
        bool isPresent = true,
        bool isEnforced = true,
        bool isTrusted = true
    ) => new($"Target_{relationship.Name}", isPresent, isEnforced, isTrusted);

    private static Task<ClosureResult> Run(
        InMemoryClosureStore store,
        ClosureRelationship[] relationships,
        params ClosureRoot[] roots
    )
    {
        var tables = roots
            .Select(root => root.Table)
            .Concat(relationships.SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable }))
            .Distinct();
        return new DependencyClosure(store).ComputeAsync(
            new ClosureRequest(
                roots,
                relationships,
                tables.ToDictionary(table => table, table => StableKeySelector.Select(table, null))
            ),
            CancellationToken.None
        );
    }

    private static async Task<(ClosureResult? Result, Exception? Error)> Outcome(Task<ClosureResult> operation)
    {
        try
        {
            return (await operation, null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }
}
