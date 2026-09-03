using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using DataPitcher.Providers.SqlServer;
using Xunit;

namespace DataPitcher.Providers.SqlServer.IntegrationTests;

[Collection("SqlServer closure")]
public sealed class SqlServerDependencyClosureTests(SqlServerClosureFixture fixture)
{
    [Fact]
    public async Task Closure_WhenChildSelected_IncludesMissingParent()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenParentSelected_ExcludesInboundChildrenByDefault()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(p, 2));
        Assert.False(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenInboundRelationshipEnabled_IncludesChildren()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p), true);
        var r = await s.RunAsync([e], s.Root(p, 2));
        Assert.True(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenOptionalForeignKeyIsNull_AddsNoParent()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,NULL)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyIsComposite_ResolvesParentByConstraintNativePosition()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,parent_first int NOT NULL,parent_second int NOT NULL,CONSTRAINT UQ_p UNIQUE(parent_second,parent_first)); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,child_first int NOT NULL,child_second int NOT NULL,CONSTRAINT FK_c_p FOREIGN KEY(child_second,child_first) REFERENCES dbo.p(parent_second,parent_first))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (9,7,8); INSERT dbo.c VALUES (1,7,8)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyReferencesUniqueConstraint_ResolvesByThatKeyRatherThanPrimaryKey()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,code nvarchar(64) NOT NULL UNIQUE); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,ref_code nvarchar(64) NOT NULL REFERENCES dbo.p(code))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (9,N'external'); INSERT dbo.c VALUES (1,N'external')");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
    }

    [Fact]
    public async Task Closure_WhenTwoForeignKeysBetweenSameTables_AppliesBothRelationships()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,code nvarchar(64) NOT NULL UNIQUE); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,bill_code nvarchar(64) NOT NULL,ship_code nvarchar(64) NOT NULL,CONSTRAINT FK_BillTo FOREIGN KEY(bill_code) REFERENCES dbo.p(code),CONSTRAINT FK_ShipTo FOREIGN KEY(ship_code) REFERENCES dbo.p(code))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (9,N'B'),(10,N'S'); INSERT dbo.c VALUES (1,N'B',N'S')");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var billTo = new ClosureRelationship(source.ForeignKey("FK_BillTo"));
        var shipTo = new ClosureRelationship(source.ForeignKey("FK_ShipTo"));
        var r = await s.RunAsync([billTo, shipTo], s.Root(c, 1));
        Assert.True(r.Contains(p, K(9)));
        Assert.True(r.Contains(p, K(10)));
    }

    [Fact]
    public async Task Closure_WhenRelationshipIsManuallyDeclared_ExpandsItLikeAForeignKey()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL)"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = ClosureRelationship.Manual("Manual_C_P", c, p, ["pid"], ["id"]);
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenSourceForeignKeyIsOrphaned_TransfersChildWithoutFabricatingParent()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL)"
        );
        await s.SourceAsync(
            "INSERT dbo.c VALUES (1,999); ALTER TABLE dbo.c WITH NOCHECK ADD CONSTRAINT FK_c_p FOREIGN KEY(pid) REFERENCES dbo.p(id)"
        );
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1));
        Assert.True(r.Contains(c, K(1)));
        Assert.False(r.Contains(p, K(2)));
        Assert.Equal([new SourceOrphanWarning(e.Name, 1)], r.Orphans);
    }

    [Fact]
    public async Task Closure_WhenParentSharedByTwoChildren_IncludesParentOnce()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.p VALUES (9); INSERT dbo.c VALUES (1,9),(2,9)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], new ClosureRoot(c, [K(1), K(2)], RootConflictPolicy.FailOnConflict));
        Assert.Single(r.Rows, x => x.Table == p && x.Key == K(9));
    }

    [Fact]
    public async Task Closure_WhenRootIsFailOnConflictAndExists_Throws()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync("CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY)");
        await s.TargetAsync("INSERT dbo.c VALUES (1)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        await Assert.ThrowsAsync<RootConflictException>(() => s.RunAsync([], s.Root(c, 1)));
    }

    [Fact]
    public async Task Closure_WhenRootIsSkipExistingAndExists_ExpandsNothing()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.SourceAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id)); INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)"
        );
        await s.TargetAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL); INSERT dbo.c VALUES (1,2)"
        );
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1, RootConflictPolicy.SkipExisting));
        Assert.False(r.Contains(c, K(1)));
        Assert.False(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenRootIsUpsertAndExists_ExpandsDependencies()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.SourceAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id)); INSERT dbo.p VALUES (2); INSERT dbo.c VALUES (1,2)"
        );
        await s.TargetAsync(
            "CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL); INSERT dbo.c VALUES (1,2)"
        );
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var e = new ClosureRelationship(s.Fk(source, c, p));
        var r = await s.RunAsync([e], s.Root(c, 1, RootConflictPolicy.Upsert));
        Assert.True(r.Contains(c, K(1)));
        Assert.True(r.Contains(p, K(2)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsBehindTrustedTargetConstraint_TerminatesItsAncestorBranch()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NOT NULL REFERENCES dbo.g(id)); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)");
        await s.TargetAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(s.Fk(source, p, g));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.False(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentDoesNotExistDespiteTrustedTargetConstraint_TransfersIt()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NOT NULL REFERENCES dbo.g(id)); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(s.Fk(source, p, g));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsButItsTargetConstraintIsAbsent_TransfersParentAnyway()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NOT NULL); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL)"
        );
        await s.BothAsync("ALTER TABLE dbo.c ADD CONSTRAINT FK_c_p FOREIGN KEY(pid) REFERENCES dbo.p(id)");
        await s.SourceAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)");
        await s.TargetAsync("INSERT dbo.p VALUES (2,3)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(new ForeignKeyDefinition("FK_p_g", p, g, ["gid"], ["id"], true, true));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsUntrusted_TransfersParentAndNamesTheConstraint()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.untrusted_parents(id))"
        );
        await s.SourceAsync(
            "INSERT dbo.untrusted_grandparents VALUES (3); INSERT dbo.untrusted_parents VALUES (2,3); INSERT dbo.c VALUES (1,2)"
        );
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("untrusted_parents").Definition;
        var g = source.Table("untrusted_grandparents").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(source.ForeignKey("FK_P_G"));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
        Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"), r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsDisabled_TransfersParentAnyway()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NULL,CONSTRAINT FK_p_g_disabled FOREIGN KEY(gid) REFERENCES dbo.g(id)); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)");
        await s.TargetAsync("INSERT dbo.p VALUES (2,NULL); ALTER TABLE dbo.p NOCHECK CONSTRAINT [FK_p_g_disabled]");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(s.Fk(source, p, g));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.True(r.Contains(p, K(2)));
        Assert.True(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenSourceConstraintMetadataDisagrees_UsesTrustedTargetConstraint()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NOT NULL); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync(
            "ALTER TABLE dbo.p WITH NOCHECK ADD CONSTRAINT FK_p_g_disagreement FOREIGN KEY(gid) REFERENCES dbo.g(id); ALTER TABLE dbo.p NOCHECK CONSTRAINT [FK_p_g_disagreement]; INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)"
        );
        await s.TargetAsync(
            "ALTER TABLE dbo.p WITH CHECK ADD CONSTRAINT FK_p_g_disagreement FOREIGN KEY(gid) REFERENCES dbo.g(id); INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3)"
        );
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(s.Fk(source, p, g));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.False(r.Contains(p, K(2)));
        Assert.False(r.Contains(g, K(3)));
    }

    [Fact]
    public async Task Closure_WhenRelationshipDisabled_ContributesNoRows()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.x (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,xid int NOT NULL REFERENCES dbo.x(id)); CREATE TABLE dbo.r (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.x VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.r VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var r = source.Table("r").Definition;
        var p = source.Table("p").Definition;
        var x = source.Table("x").Definition;
        var rp = new ClosureRelationship(s.Fk(source, r, p));
        var px = new ClosureRelationship(s.Fk(source, p, x), false, false);
        var result = await s.RunAsync([rp, px], s.Root(r, 1));
        Assert.True(result.Contains(p, K(2)));
        Assert.False(result.Contains(x, K(3)));
    }

    [Fact]
    public async Task Closure_WhenGraphHasTwoTableCycle_Terminates()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.SourceAsync(
            "INSERT dbo.cycle_a VALUES (1,NULL); INSERT dbo.cycle_b VALUES (2,1); UPDATE dbo.cycle_a SET b_id=2 WHERE id=1"
        );
        var (source, target) = await s.CatalogsAsync();
        var a = source.Table("cycle_a").Definition;
        var b = source.Table("cycle_b").Definition;
        var ab = new ClosureRelationship(s.Fk(source, a, b));
        var ba = new ClosureRelationship(s.Fk(source, b, a));
        var r = await s.RunAsync([ab, ba], s.Root(a, 1));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenTableIsSelfReferencing_FollowsHierarchyAndTerminates()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.SourceAsync("INSERT dbo.employees VALUES (1,1); INSERT dbo.employees VALUES (2,1)");
        var (source, target) = await s.CatalogsAsync();
        var eTable = source.Table("employees").Definition;
        var e = new ClosureRelationship(s.Fk(source, eTable, eTable));
        var r = await s.RunAsync([e], s.Root(eTable, 2));
        Assert.True(r.Contains(eTable, K(1)));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenChainExpands_StampsBreadthFirstGenerations()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.g (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.p (id int NOT NULL PRIMARY KEY,gid int NOT NULL REFERENCES dbo.g(id)); CREATE TABLE dbo.c (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.p(id))"
        );
        await s.SourceAsync("INSERT dbo.g VALUES (3); INSERT dbo.p VALUES (2,3); INSERT dbo.c VALUES (1,2)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(s.Fk(source, c, p));
        var pg = new ClosureRelationship(s.Fk(source, p, g));
        var r = await s.RunAsync([cp, pg], s.Root(c, 1));
        Assert.Equal(0, Assert.Single(r.Rows, row => row.Table == c).Generation);
        Assert.Equal(1, Assert.Single(r.Rows, row => row.Table == p).Generation);
        Assert.Equal(2, Assert.Single(r.Rows, row => row.Table == g).Generation);
    }

    [Fact]
    public async Task Closure_WhenEnabledParticipantIsBlocked_RejectsRequestBeforeSeeding()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        var c = T("C");
        var blocked = new TableDefinition("dbo", "Blocked", [], null, []);
        var e = new ClosureRelationship(F(c, blocked));
        await using var store = s.CountingStore();
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
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenParticipantHasNoStableKeySelection_RejectsRequestBeforeSeeding()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        var c = T("C");
        await using var store = s.CountingStore();
        var request = new ClosureRequest([Root(c, 1)], [], new Dictionary<TableDefinition, StableKeySelection>());
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenRootUsesExplicitNonNullableUniqueStableKey_IncludesIt()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync("CREATE TABLE dbo.c (id int NOT NULL CONSTRAINT UQ_Code UNIQUE)");
        await s.SourceAsync("INSERT dbo.c VALUES (1)");
        var (source, target) = await s.CatalogsAsync();
        var c = source.Table("c").Definition;
        var unique = source.Table("c").Definition.UniqueConstraints.Single(x => x.Name == "UQ_Code");
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [c] = StableKeySelector.Select(c, "UQ_Code"),
        };
        var r = await s.RunAsync([], new ClosureRoot(c, [K(1)], RootConflictPolicy.FailOnConflict), selections);
        Assert.True(r.Contains(c, K(1)));
    }

    [Fact]
    public async Task Closure_WhenSelectedUniqueContainsNullableColumn_RejectsRequestBeforeSeeding()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        var c = new TableDefinition("dbo", "C", [new ColumnDefinition("Code", typeof(string), true)], null, [unique]);
        await using var store = s.CountingStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = StableKeySelector.Select(c, "UQ_Code") }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenSelectionDirectlyCarriesNonPrimaryKeyConstraintWithNullableColumn_RejectsRequestBeforeSeeding()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        var nonPrimaryConstraint = new UniqueConstraint("UQ_Other", ["Code"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), true)],
            null,
            [nonPrimaryConstraint]
        );
        await using var store = s.CountingStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = new StableKeySelection(nonPrimaryConstraint) }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenSelectedConstraintReferencesColumnAbsentFromTable_RejectsRequestBeforeSeeding()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        var constraint = new UniqueConstraint("UQ_Missing", ["MissingColumn"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), false)],
            null,
            [constraint]
        );
        await using var store = s.CountingStore();
        var request = new ClosureRequest(
            [Root(c, 1)],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = new StableKeySelection(constraint) }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.x (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.a (id int NOT NULL PRIMARY KEY,xid int NULL REFERENCES dbo.x(id)); CREATE TABLE dbo.b (id int NOT NULL PRIMARY KEY,xid int NOT NULL REFERENCES dbo.x(id)); CREATE TABLE dbo.r (id int NOT NULL PRIMARY KEY,aid int NOT NULL REFERENCES dbo.a(id),bid int NOT NULL REFERENCES dbo.b(id))"
        );
        await s.SourceAsync(
            "INSERT dbo.x VALUES (4); INSERT dbo.a VALUES (2,4); INSERT dbo.b VALUES (3,4); INSERT dbo.r VALUES (1,2,3)"
        );
        await s.TargetAsync("INSERT dbo.a VALUES (2,NULL)");
        var (source, target) = await s.CatalogsAsync();
        var r = source.Table("r").Definition;
        var a = source.Table("a").Definition;
        var b = source.Table("b").Definition;
        var x = source.Table("x").Definition;
        var ra = new ClosureRelationship(s.Fk(source, r, a));
        var rb = new ClosureRelationship(s.Fk(source, r, b));
        var ax = new ClosureRelationship(s.Fk(source, a, x));
        var bx = new ClosureRelationship(s.Fk(source, b, x));
        var result = await s.RunAsync([ra, rb, ax, bx], s.Root(r, 1));
        Assert.True(result.Contains(x, K(4)));
    }

    [Fact]
    public async Task Closure_WhenAncestorDemandedByTwoIncludedPaths_AppearsExactlyOnce()
    {
        await using var s = await SqlServerClosureScenario.CreateAsync(fixture);
        await s.BothAsync(
            "CREATE TABLE dbo.x (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.a (id int NOT NULL PRIMARY KEY,xid int NOT NULL REFERENCES dbo.x(id)); CREATE TABLE dbo.b (id int NOT NULL PRIMARY KEY,xid int NOT NULL REFERENCES dbo.x(id)); CREATE TABLE dbo.r (id int NOT NULL PRIMARY KEY,aid int NOT NULL REFERENCES dbo.a(id),bid int NOT NULL REFERENCES dbo.b(id))"
        );
        await s.SourceAsync(
            "INSERT dbo.x VALUES (4); INSERT dbo.a VALUES (2,4); INSERT dbo.b VALUES (3,4); INSERT dbo.r VALUES (1,2,3)"
        );
        var (source, target) = await s.CatalogsAsync();
        var r = source.Table("r").Definition;
        var a = source.Table("a").Definition;
        var b = source.Table("b").Definition;
        var x = source.Table("x").Definition;
        var ra = new ClosureRelationship(s.Fk(source, r, a));
        var rb = new ClosureRelationship(s.Fk(source, r, b));
        var ax = new ClosureRelationship(s.Fk(source, a, x));
        var bx = new ClosureRelationship(s.Fk(source, b, x));
        var result = await s.RunAsync([ra, rb, ax, bx], s.Root(r, 1));
        Assert.Single(result.Rows, row => row.Table == x && row.Key == K(4));
    }

    private static TableDefinition T(string name, string[]? uniqueColumns = null) =>
        new(
            "dbo",
            name,
            [new ColumnDefinition("id", typeof(int), false)],
            new UniqueConstraint($"PK_{name}", ["id"]),
            uniqueColumns is null ? [] : [new UniqueConstraint($"UQ_{name}", uniqueColumns)]
        );

    private static StableKey K(params object?[] values) =>
        new(values.Select((value, index) => new KeyComponent(index == 0 ? "id" : $"id{index + 1}", value)));

    private static ForeignKeyDefinition F(TableDefinition child, TableDefinition parent) =>
        new($"FK_{child.Name}_{parent.Name}", child, parent, ["id"], ["id"], true, true);

    private static ClosureRoot Root(
        TableDefinition table,
        int key,
        RootConflictPolicy policy = RootConflictPolicy.FailOnConflict
    ) => new(table, [K(key)], policy);
}

internal sealed class SqlServerClosureScenario : IAsyncDisposable
{
    private readonly SqlServerClosureScope _scope;

    private SqlServerClosureScenario(SqlServerClosureScope scope) => _scope = scope;

    public string TargetAdminConnectionString => _scope.TargetAdminConnectionString;

    public static async Task<SqlServerClosureScenario> CreateAsync(SqlServerClosureFixture fixture) =>
        new(await fixture.CreateScopeAsync());

    public Task SourceAsync(string sql) => _scope.ExecuteAsync(sql);

    public Task TargetAsync(string sql) => _scope.ExecuteTargetAsync(sql);

    public async Task BothAsync(string sql)
    {
        await SourceAsync(sql);
        await TargetAsync(sql);
    }

    public async Task<(SqlServerSchemaSnapshot Source, SqlServerSchemaSnapshot Target)> CatalogsAsync() =>
        (
            await new SqlServerCatalogReader(_scope.SourceConnectionString).ReadAsync("dbo", CancellationToken.None),
            await new SqlServerCatalogReader(_scope.TargetConnectionString).ReadAsync("dbo", CancellationToken.None)
        );

    public ForeignKeyDefinition Fk(SqlServerSchemaSnapshot catalog, TableDefinition child, TableDefinition parent) =>
        catalog.ForeignKeys.Single(foreignKey => foreignKey.ChildTable == child && foreignKey.ParentTable == parent);

    public ClosureRoot Root(
        TableDefinition table,
        int id,
        RootConflictPolicy policy = RootConflictPolicy.FailOnConflict
    ) => new(table, [Key(id)], policy);

    public async Task<ClosureResult> RunAsync(
        IReadOnlyCollection<ClosureRelationship> relationships,
        params ClosureRoot[] roots
    ) => await RunAsync(relationships, roots, null);

    public Task<ClosureResult> RunAsync(
        IReadOnlyCollection<ClosureRelationship> relationships,
        ClosureRoot root,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> selections
    ) => RunAsync(relationships, [root], selections);

    public async Task<ClosureResult> RunAsync(
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyCollection<ClosureRoot> roots,
        IReadOnlyDictionary<TableDefinition, StableKeySelection>? selections
    )
    {
        var (source, target) = await CatalogsAsync();
        selections ??= roots
            .Select(root => root.Table)
            .Concat(relationships.SelectMany(relationship => new[] { relationship.FromTable, relationship.ToTable }))
            .Distinct()
            .ToDictionary(table => table, table => StableKeySelector.Select(table, null));
        await using var store = new SqlServerClosureStore(
            _scope.SourceConnectionString,
            _scope.TargetConnectionString,
            source,
            target,
            selections
        );
        return await new DependencyClosure(store).ComputeAsync(
            new ClosureRequest(roots, relationships, selections),
            CancellationToken.None
        );
    }

    public CountingClosureStore CountingStore() =>
        new(
            new SqlServerClosureStore(
                _scope.SourceConnectionString,
                _scope.TargetConnectionString,
                new SqlServerSchemaSnapshot([], []),
                new SqlServerSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );

    public async Task CreateBatchChainAsync(int count)
    {
        await BothAsync(
            "CREATE TABLE dbo.batch_parent (id int NOT NULL PRIMARY KEY); CREATE TABLE dbo.batch_child (id int NOT NULL PRIMARY KEY,pid int NOT NULL REFERENCES dbo.batch_parent(id))"
        );
        var parents = string.Join(",", Enumerable.Range(1, count).Select(id => $"({id})"));
        var children = string.Join(",", Enumerable.Range(1, count).Select(id => $"({id},{id})"));
        await SourceAsync($"INSERT dbo.batch_parent VALUES {parents}; INSERT dbo.batch_child VALUES {children}");
    }

    public async Task<ClosureResult> RunBatchAsync()
    {
        var (source, target) = await CatalogsAsync();
        var child = source.Table("batch_child").Definition;
        var parent = source.Table("batch_parent").Definition;
        var relationship = new ClosureRelationship(Fk(source, child, parent));
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [child] = StableKeySelector.Select(child, null),
            [parent] = StableKeySelector.Select(parent, null),
        };
        var roots = Enumerable
            .Range(1, 40)
            .Select(id => new ClosureRoot(child, [Key(id)], RootConflictPolicy.FailOnConflict));
        await using var store = new SqlServerClosureStore(
            _scope.SourceConnectionString,
            _scope.TargetConnectionString,
            source,
            target,
            selections
        );
        return await new DependencyClosure(store).ComputeAsync(
            new ClosureRequest(roots, [relationship], selections),
            CancellationToken.None
        );
    }

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private static StableKey Key(int id) => new([new KeyComponent("id", id)]);
}

internal sealed class CountingClosureStore(IClosureStore inner) : IClosureStore, IAsyncDisposable
{
    public int SeedCalls { get; private set; }

    public Task<IReadOnlyCollection<StableKey>> SeedRootKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    )
    {
        SeedCalls++;
        return inner.SeedRootKeysAsync(table, keys, cancellationToken);
    }

    public Task<IReadOnlyDictionary<StableKey, TargetProbe>> ProbeTargetAsync(
        TableDefinition table,
        IReadOnlyCollection<ClosureRelationship> outgoingRelationships,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    ) => inner.ProbeTargetAsync(table, outgoingRelationships, keys, cancellationToken);

    public Task<ClosureExpansion> ExpandAsync(
        ClosureRelationship relationship,
        IReadOnlyCollection<StableKey> fromKeys,
        CancellationToken cancellationToken
    ) => inner.ExpandAsync(relationship, fromKeys, cancellationToken);

    public Task<IReadOnlyCollection<StableKey>> InsertNewKeysAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        int generation,
        CancellationToken cancellationToken
    ) => inner.InsertNewKeysAsync(table, keys, generation, cancellationToken);

    public Task MarkIncludedAsync(
        TableDefinition table,
        IReadOnlyCollection<StableKey> keys,
        CancellationToken cancellationToken
    ) => inner.MarkIncludedAsync(table, keys, cancellationToken);

    public ValueTask DisposeAsync() =>
        inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
