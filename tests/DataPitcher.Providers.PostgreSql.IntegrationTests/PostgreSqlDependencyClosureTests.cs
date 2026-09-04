using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

// Real-engine re-run of tests/DataPitcher.UnitTests/Closure/DependencyClosureTests.cs lines 5-35
// (the 31 behavioural fixtures). Every assertion below is unchanged from that file: same
// Assert.True/False/Single/Equal/Contains/ThrowsAsync calls, same logical claim. Only the setup
// changes: real PostgreSQL tables, rows, and constraints replace the in-memory fake's Link/AddRow/
// MarkTarget/SetTargetConstraint calls. Tests that in the fake never reach the store (blocked-table
// and no-stable-key rejections) reuse the fake's exact synthetic TableDefinition fixtures verbatim,
// since no database interaction ever occurs for them either way.
public sealed class PostgreSqlDependencyClosureTests : IClassFixture<PostgreSqlClosureFixture>
{
    private readonly PostgreSqlClosureFixture _fixture;

    public PostgreSqlDependencyClosureTests(PostgreSqlClosureFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Closure_WhenChildSelected_IncludesMissingParent()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
    }

    [Fact]
    public async Task Closure_WhenParentSelected_ExcludesInboundChildrenByDefault()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(p, Key("id", 2)));
        Assert.False(r.Contains(c, Key("id", 1)));
    }

    [Fact]
    public async Task Closure_WhenInboundRelationshipEnabled_IncludesChildren()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p), isInbound: true);
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(p, Key("id", 2)));
        Assert.True(r.Contains(c, Key("id", 1)));
    }

    [Fact]
    public async Task Closure_WhenOptionalForeignKeyIsNull_AddsNoParent()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT INTO customers VALUES (2,'X'); INSERT INTO optional_orders VALUES (1,NULL);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("optional_orders").Definition;
        var p = source.Table("customers").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.False(r.Contains(p, Key("customer_id", 2)));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyIsComposite_ResolvesParentByConstraintNativePosition()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO composite_parent VALUES (7,8); INSERT INTO composite_child VALUES (1,8,7);"
        );
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("composite_child").Definition;
        var p = source.Table("composite_parent").Definition;
        var relationship = new ClosureRelationship(source.ForeignKey("fk_composite_child_parent"));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, new StableKey([new("left_value", 7), new("right_value", 8)])));
    }

    [Fact]
    public async Task Closure_WhenForeignKeyReferencesUniqueConstraint_ResolvesByThatKeyRatherThanPrimaryKey()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync(
            "INSERT INTO external_parents VALUES (9,'external'); INSERT INTO external_children VALUES (1,'external');"
        );
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("external_children").Definition;
        var p = source.Table("external_parents").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 9)));
    }

    [Fact]
    public async Task Closure_WhenTwoForeignKeysBetweenSameTables_AppliesBothRelationships()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE dual_parent (code text PRIMARY KEY)");
        await BothAsync(
            scope,
            "CREATE TABLE dual_child (id integer PRIMARY KEY, bill_code text NOT NULL, ship_code text NOT NULL)"
        );
        await BothAsync(
            scope,
            "ALTER TABLE dual_child ADD CONSTRAINT fk_dual_bill FOREIGN KEY (bill_code) REFERENCES dual_parent(code)"
        );
        await BothAsync(
            scope,
            "ALTER TABLE dual_child ADD CONSTRAINT fk_dual_ship FOREIGN KEY (ship_code) REFERENCES dual_parent(code)"
        );
        await scope.ExecuteAsync(
            "INSERT INTO dual_parent VALUES ('B'),('S'); INSERT INTO dual_child VALUES (1,'B','S');"
        );
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("dual_child").Definition;
        var p = source.Table("dual_parent").Definition;
        var billTo = new ClosureRelationship(source.ForeignKey("fk_dual_bill"));
        var shipTo = new ClosureRelationship(source.ForeignKey("fk_dual_ship"));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [billTo, shipTo], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("code", "B")));
        Assert.True(r.Contains(p, Key("code", "S")));
    }

    // FINDING: PostgreSqlClosureStore.ExpandAsync dereferences relationship.ForeignKey unconditionally
    // (JoinColumns), which is null for ClosureRelationship.Manual. This test therefore fails against
    // the real store with a NullReferenceException instead of expanding the manual relationship, even
    // though section 8 of dependency-semantics.md requires manual relationships to "participate in the
    // closure identically to constraint-derived ones." The assertion below is unchanged from the fake.
    [Fact]
    public async Task Closure_WhenRelationshipIsManuallyDeclared_ExpandsItLikeAForeignKey()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL)");
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = ClosureRelationship.Manual("Manual_C_P", c, p, ["pid"], ["id"]);
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
    }

    [Fact]
    public async Task Closure_WhenSourceForeignKeyIsOrphaned_TransfersChildWithoutFabricatingParent()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL)");
        await scope.ExecuteAsync("INSERT INTO c VALUES (1,999);"); // real orphan: no matching p row
        await scope.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT fk_c_p FOREIGN KEY (pid) REFERENCES p(id) NOT VALID"); // NOT VALID skips checking the pre-existing orphan row; new inserts are still checked
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(store, [relationship], Selections(c, p), Root(c, Key("id", 1)));
        Assert.True(r.Contains(c, Key("id", 1)));
        Assert.False(r.Contains(p, Key("id", 999)));
        Assert.Equal([new SourceOrphanWarning(relationship.Name, 1)], r.Orphans);
    }

    [Fact]
    public async Task Closure_WhenParentSharedByTwoChildren_IncludesParentOnce()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO p VALUES (9); INSERT INTO c VALUES (1,9),(2,9);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(
            store,
            [relationship],
            Selections(c, p),
            new ClosureRoot(c, [Key("id", 1), Key("id", 2)], RootConflictPolicy.FailOnConflict)
        );
        Assert.Single(r.Rows, x => x.Table == p && x.Key == Key("id", 9));
    }

    [Fact]
    public async Task Closure_WhenRootIsFailOnConflictAndExists_Throws()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY)");
        await scope.ExecuteTargetAsync("INSERT INTO c VALUES (1)");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, target, Selections(c));
        await Assert.ThrowsAsync<RootConflictException>(() =>
            RunAsync(store, [], Selections(c), Root(c, Key("id", 1)))
        );
    }

    [Fact]
    public async Task Closure_WhenRootIsSkipExistingAndExists_ExpandsNothing()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        await scope.ExecuteTargetAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(
            store,
            [relationship],
            Selections(c, p),
            Root(c, Key("id", 1), RootConflictPolicy.SkipExisting)
        );
        Assert.False(r.Contains(c, Key("id", 1)));
        Assert.False(r.Contains(p, Key("id", 2)));
    }

    [Fact]
    public async Task Closure_WhenRootIsUpsertAndExists_ExpandsDependencies()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY)");
        await scope.ExecuteAsync("CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteTargetAsync("CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL)"); // target has no FK on c: c(1) exists there without requiring p to exist too
        await scope.ExecuteAsync("INSERT INTO p VALUES (2); INSERT INTO c VALUES (1,2);");
        await scope.ExecuteTargetAsync("INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var relationship = new ClosureRelationship(Fk(source, c, p));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p)
        );
        var r = await RunAsync(
            store,
            [relationship],
            Selections(c, p),
            Root(c, Key("id", 1), RootConflictPolicy.Upsert)
        );
        Assert.True(r.Contains(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsBehindTrustedTargetConstraint_TerminatesItsAncestorBranch()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NOT NULL REFERENCES g(id))");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);");
        await scope.ExecuteTargetAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3);"); // real, validated, enforced target FK: p(2)'s gid genuinely resolves to g(3)
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(Fk(source, p, g));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.False(r.Contains(p, Key("id", 2)));
        Assert.False(r.Contains(g, Key("id", 3)));
    }

    [Fact]
    public async Task Closure_WhenParentDoesNotExistDespiteTrustedTargetConstraint_TransfersIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NOT NULL REFERENCES g(id))");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);");
        // target has no rows at all: p does not exist, so its (validated, enforced) constraint is never even probed.
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(Fk(source, p, g));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
        Assert.True(r.Contains(g, Key("id", 3)));
    }

    [Fact]
    public async Task Closure_WhenParentExistsButItsTargetConstraintIsAbsent_TransfersParentAnyway()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NOT NULL)"); // no FK constraint on p->g in either database
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL)");
        await scope.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT fk_c_p FOREIGN KEY (pid) REFERENCES p(id)");
        await scope.ExecuteTargetAsync("ALTER TABLE c ADD CONSTRAINT fk_c_p FOREIGN KEY (pid) REFERENCES p(id)");
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);");
        await scope.ExecuteTargetAsync("INSERT INTO p VALUES (2,3);"); // p present in target, but p->g has no constraint at all: absent
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(new ForeignKeyDefinition("FK_P_G", p, g, ["gid"], ["id"], true, true));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
        Assert.True(r.Contains(g, Key("id", 3)));
    }

    // THE UNTRUSTED-CONSTRAINT high-value fixture: uses the fixed `untrusted_parents`/`untrusted_grandparents`
    // schema from PostgreSqlClosureFixture, whose target already carries a real NOT VALID constraint named
    // Target_FK_P_G with a real pre-seeded row (2,3). Nothing here is simulated.
    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsUntrusted_TransfersParentAndNamesTheConstraint()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(
            scope,
            "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES untrusted_parents(id))"
        );
        await scope.ExecuteAsync(
            "INSERT INTO untrusted_grandparents VALUES (3); INSERT INTO untrusted_parents VALUES (2,3); INSERT INTO c VALUES (1,2);"
        );
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("untrusted_parents").Definition;
        var g = source.Table("untrusted_grandparents").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(source.ForeignKey("FK_P_G"));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
        Assert.True(r.Contains(g, Key("id", 3)));
        Assert.Contains(new TargetConstraintWarning($"Target_{pg.Name}"), r.Warnings);
    }

    [Fact]
    public async Task Closure_WhenParentTargetConstraintIsDisabled_TransfersParentAnyway()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NULL REFERENCES g(id))");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);");
        await scope.ExecuteTargetAsync("INSERT INTO p VALUES (2,NULL); ALTER TABLE p DISABLE TRIGGER ALL;"); // g absent from target: the parent row's own FK value is NULL, so disabling enforcement is what we are testing, not row-level referential integrity
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(Fk(source, p, g));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.True(r.Contains(p, Key("id", 2)));
        Assert.True(r.Contains(g, Key("id", 3)));
    }

    [Fact]
    public async Task Closure_WhenSourceConstraintMetadataDisagrees_UsesTrustedTargetConstraint()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NOT NULL)");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync(
            "ALTER TABLE p ADD CONSTRAINT fk_p_g FOREIGN KEY (gid) REFERENCES g(id) NOT VALID; ALTER TABLE p DISABLE TRIGGER ALL;"
        );
        await scope.ExecuteTargetAsync("ALTER TABLE p ADD CONSTRAINT fk_p_g FOREIGN KEY (gid) REFERENCES g(id)"); // validated, enforced on target
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);"); // source's own FK is disabled+unvalidated
        await scope.ExecuteTargetAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(Fk(source, p, g)); // source metadata reports IsEnforced=false, IsTrusted=false
        Assert.False(pg.ForeignKey!.IsEnforced);
        Assert.False(pg.ForeignKey!.IsTrusted);
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.False(r.Contains(p, Key("id", 2)));
        Assert.False(r.Contains(g, Key("id", 3)));
    }

    [Fact]
    public async Task Closure_WhenRelationshipDisabled_ContributesNoRows()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE x (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, xid integer NOT NULL REFERENCES x(id))");
        await BothAsync(scope, "CREATE TABLE r (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO x VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO r VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var root = source.Table("r").Definition;
        var p = source.Table("p").Definition;
        var x = source.Table("x").Definition;
        var rp = new ClosureRelationship(Fk(source, root, p));
        var px = new ClosureRelationship(Fk(source, p, x), isInbound: false, isEnabled: false);
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(root, p, x)
        );
        var result = await RunAsync(store, [rp, px], Selections(root, p, x), Root(root, Key("id", 1)));
        Assert.True(result.Contains(p, Key("id", 2)));
        Assert.False(result.Contains(x, Key("id", 3)));
    }

    [Fact]
    public async Task Closure_WhenGraphHasTwoTableCycle_Terminates()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        // cycle_a.b_id is validated by an immediate (non-NOT VALID) constraint, so the cyclic pair
        // must be inserted with the forward reference null first, then closed with an UPDATE.
        await scope.ExecuteAsync(
            "INSERT INTO cycle_a VALUES (1,NULL); INSERT INTO cycle_b VALUES (2,1); UPDATE cycle_a SET b_id = 2 WHERE id = 1;"
        );
        var (source, target) = await ReadAsync(scope);
        var a = source.Table("cycle_a").Definition;
        var b = source.Table("cycle_b").Definition;
        var ab = new ClosureRelationship(Fk(source, a, b));
        var ba = new ClosureRelationship(Fk(source, b, a));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(a, b)
        );
        var r = await RunAsync(store, [ab, ba], Selections(a, b), Root(a, Key("id", 1)));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenTableIsSelfReferencing_FollowsHierarchyAndTerminates()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT INTO employees VALUES (1,1); INSERT INTO employees VALUES (2,1);");
        var (source, target) = await ReadAsync(scope);
        var e = source.Table("employees").Definition;
        var relationship = new ClosureRelationship(Fk(source, e, e));
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, target, Selections(e));
        var r = await RunAsync(store, [relationship], Selections(e), Root(e, Key("id", 2)));
        Assert.True(r.Contains(e, Key("id", 1)));
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Closure_WhenChainExpands_StampsBreadthFirstGenerations()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE g (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE p (id integer PRIMARY KEY, gid integer NOT NULL REFERENCES g(id))");
        await BothAsync(scope, "CREATE TABLE c (id integer PRIMARY KEY, pid integer NOT NULL REFERENCES p(id))");
        await scope.ExecuteAsync("INSERT INTO g VALUES (3); INSERT INTO p VALUES (2,3); INSERT INTO c VALUES (1,2);");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("c").Definition;
        var p = source.Table("p").Definition;
        var g = source.Table("g").Definition;
        var cp = new ClosureRelationship(Fk(source, c, p));
        var pg = new ClosureRelationship(Fk(source, p, g));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(c, p, g)
        );
        var r = await RunAsync(store, [cp, pg], Selections(c, p, g), Root(c, Key("id", 1)));
        Assert.Equal(0, Assert.Single(r.Rows, row => row.Table == c).Generation);
        Assert.Equal(1, Assert.Single(r.Rows, row => row.Table == p).Generation);
        Assert.Equal(2, Assert.Single(r.Rows, row => row.Table == g).Generation);
    }

    [Fact]
    public async Task Closure_WhenEnabledParticipantIsBlocked_RejectsRequestBeforeSeeding()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var c = T("C");
        var blocked = new TableDefinition("dbo", "Blocked", [], null, []);
        var e = new ClosureRelationship(F(c, blocked));
        await using var store = new CountingClosureStore(
            new PostgreSqlClosureStore(
                scope.Source,
                scope.Target,
                new PostgreSqlSchemaSnapshot([], []),
                new PostgreSqlSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );
        var request = new ClosureRequest(
            [Root(c, K(1))],
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
        await using var scope = await _fixture.CreateScopeAsync();
        var c = T("C");
        await using var store = new CountingClosureStore(
            new PostgreSqlClosureStore(
                scope.Source,
                scope.Target,
                new PostgreSqlSchemaSnapshot([], []),
                new PostgreSqlSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );
        var request = new ClosureRequest([Root(c, K(1))], [], new Dictionary<TableDefinition, StableKeySelection>());
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    [Fact]
    public async Task Closure_WhenRootUsesExplicitNonNullableUniqueStableKey_IncludesIt()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await scope.ExecuteAsync("INSERT INTO unique_only VALUES ('abc');");
        var (source, target) = await ReadAsync(scope);
        var c = source.Table("unique_only").Definition;
        var constraintName = Assert.Single(c.UniqueConstraints).Name;
        var selections = new Dictionary<TableDefinition, StableKeySelection>
        {
            [c] = StableKeySelector.Select(c, constraintName),
        };
        await using var store = new PostgreSqlClosureStore(scope.Source, scope.Target, source, target, selections);
        var request = new ClosureRequest([Root(c, Key("code", "abc"))], [], selections);
        var r = await new DependencyClosure(store).ComputeAsync(request, CancellationToken.None);
        Assert.True(r.Contains(c, Key("code", "abc")));
    }

    [Fact]
    public async Task Closure_WhenSelectedUniqueContainsNullableColumn_RejectsRequestBeforeSeeding()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        var unique = new UniqueConstraint("UQ_Code", ["Code"]);
        var c = new TableDefinition("dbo", "C", [new ColumnDefinition("Code", typeof(string), true)], null, [unique]);
        await using var store = new CountingClosureStore(
            new PostgreSqlClosureStore(
                scope.Source,
                scope.Target,
                new PostgreSqlSchemaSnapshot([], []),
                new PostgreSqlSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );
        var request = new ClosureRequest(
            [Root(c, K(1))],
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
        await using var scope = await _fixture.CreateScopeAsync();
        var nonPrimaryConstraint = new UniqueConstraint("UQ_Other", ["Code"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), true)],
            null,
            [nonPrimaryConstraint]
        );
        await using var store = new CountingClosureStore(
            new PostgreSqlClosureStore(
                scope.Source,
                scope.Target,
                new PostgreSqlSchemaSnapshot([], []),
                new PostgreSqlSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );
        var request = new ClosureRequest(
            [Root(c, K(1))],
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
        await using var scope = await _fixture.CreateScopeAsync();
        var constraint = new UniqueConstraint("UQ_Missing", ["MissingColumn"]);
        var c = new TableDefinition(
            "dbo",
            "C",
            [new ColumnDefinition("Code", typeof(string), false)],
            null,
            [constraint]
        );
        await using var store = new CountingClosureStore(
            new PostgreSqlClosureStore(
                scope.Source,
                scope.Target,
                new PostgreSqlSchemaSnapshot([], []),
                new PostgreSqlSchemaSnapshot([], []),
                new Dictionary<TableDefinition, StableKeySelection>()
            )
        );
        var request = new ClosureRequest(
            [Root(c, K(1))],
            [],
            new Dictionary<TableDefinition, StableKeySelection> { [c] = new StableKeySelection(constraint) }
        );
        await Assert.ThrowsAsync<BlockedTableException>(() =>
            new DependencyClosure(store).ComputeAsync(request, CancellationToken.None)
        );
        Assert.Equal(0, store.SeedCalls);
    }

    // THE SATISFIED-PATH DIAMOND: root r depends on a and b, both depend on x. a is present in the
    // target behind a real, validated, enforced foreign key (a.xid is NULL there — a NULL foreign key
    // value is exempt from referential-integrity checking in PostgreSQL, so the constraint validates
    // without a matching target x row; DataPitcher's satisfaction rule is genuinely constraint-level,
    // not row-value-level, exactly as section 5 documents). b is entirely missing from the target. x
    // itself has no row in the target. x must still be transferred through b's path.
    [Fact]
    public async Task Closure_WhenParentSatisfiedOnOnePathButRequiredOnAnother_StillTransfersSharedAncestor()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE x (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE a (id integer PRIMARY KEY, xid integer NULL REFERENCES x(id))");
        await BothAsync(scope, "CREATE TABLE b (id integer PRIMARY KEY, xid integer NOT NULL REFERENCES x(id))");
        await BothAsync(
            scope,
            "CREATE TABLE r (id integer PRIMARY KEY, aid integer NOT NULL REFERENCES a(id), bid integer NOT NULL REFERENCES b(id))"
        );
        await scope.ExecuteAsync(
            "INSERT INTO x VALUES (4); INSERT INTO a VALUES (2,4); INSERT INTO b VALUES (3,4); INSERT INTO r VALUES (1,2,3);"
        );
        await scope.ExecuteTargetAsync("INSERT INTO a VALUES (2,NULL);"); // a present and its (validated) constraint is satisfied without needing x to exist in target
        var (source, target) = await ReadAsync(scope);
        var root = source.Table("r").Definition;
        var a = source.Table("a").Definition;
        var b = source.Table("b").Definition;
        var x = source.Table("x").Definition;
        var ra = new ClosureRelationship(Fk(source, root, a));
        var rb = new ClosureRelationship(Fk(source, root, b));
        var ax = new ClosureRelationship(Fk(source, a, x));
        var bx = new ClosureRelationship(Fk(source, b, x));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(root, a, b, x)
        );
        var result = await RunAsync(store, [ra, rb, ax, bx], Selections(root, a, b, x), Root(root, Key("id", 1)));
        Assert.True(result.Contains(x, Key("id", 4)));
    }

    // THE TWO-PATH DIAMOND: x demanded by two independently-expanded included paths (a and b, neither
    // present in the target), must appear exactly once.
    [Fact]
    public async Task Closure_WhenAncestorDemandedByTwoIncludedPaths_AppearsExactlyOnce()
    {
        await using var scope = await _fixture.CreateScopeAsync();
        await BothAsync(scope, "CREATE TABLE x (id integer PRIMARY KEY)");
        await BothAsync(scope, "CREATE TABLE a (id integer PRIMARY KEY, xid integer NOT NULL REFERENCES x(id))");
        await BothAsync(scope, "CREATE TABLE b (id integer PRIMARY KEY, xid integer NOT NULL REFERENCES x(id))");
        await BothAsync(
            scope,
            "CREATE TABLE r (id integer PRIMARY KEY, aid integer NOT NULL REFERENCES a(id), bid integer NOT NULL REFERENCES b(id))"
        );
        await scope.ExecuteAsync(
            "INSERT INTO x VALUES (4); INSERT INTO a VALUES (2,4); INSERT INTO b VALUES (3,4); INSERT INTO r VALUES (1,2,3);"
        );
        var (source, target) = await ReadAsync(scope);
        var root = source.Table("r").Definition;
        var a = source.Table("a").Definition;
        var b = source.Table("b").Definition;
        var x = source.Table("x").Definition;
        var ra = new ClosureRelationship(Fk(source, root, a));
        var rb = new ClosureRelationship(Fk(source, root, b));
        var ax = new ClosureRelationship(Fk(source, a, x));
        var bx = new ClosureRelationship(Fk(source, b, x));
        await using var store = new PostgreSqlClosureStore(
            scope.Source,
            scope.Target,
            source,
            target,
            Selections(root, a, b, x)
        );
        var result = await RunAsync(store, [ra, rb, ax, bx], Selections(root, a, b, x), Root(root, Key("id", 1)));
        Assert.Single(result.Rows, row => row.Table == x && row.Key == Key("id", 4));
    }

    private static async Task BothAsync(PostgreSqlClosureScope scope, string sql)
    {
        await scope.ExecuteAsync(sql);
        await scope.ExecuteTargetAsync(sql);
    }

    private static async Task<(PostgreSqlSchemaSnapshot Source, PostgreSqlSchemaSnapshot Target)> ReadAsync(
        PostgreSqlClosureScope scope
    )
    {
        var source = await new PostgreSqlCatalogReader(scope.Source).ReadAsync(scope.Schema, CancellationToken.None);
        var target = await new PostgreSqlCatalogReader(scope.Target).ReadAsync(scope.Schema, CancellationToken.None);
        return (source, target);
    }

    private static ForeignKeyDefinition Fk(
        PostgreSqlSchemaSnapshot catalog,
        TableDefinition child,
        TableDefinition parent
    ) => catalog.ForeignKeys.Single(x => x.ChildTable == child && x.ParentTable == parent);

    private static IReadOnlyDictionary<TableDefinition, StableKeySelection> Selections(
        params TableDefinition[] tables
    ) => tables.Distinct().ToDictionary(t => t, t => StableKeySelector.Select(t, null));

    private static ClosureRoot Root(
        TableDefinition table,
        StableKey key,
        RootConflictPolicy policy = RootConflictPolicy.FailOnConflict
    ) => new(table, [key], policy);

    private static StableKey Key(string column, object? value) => new([new KeyComponent(column, value)]);

    private static Task<ClosureResult> RunAsync(
        IClosureStore store,
        IReadOnlyCollection<ClosureRelationship> relationships,
        IReadOnlyDictionary<TableDefinition, StableKeySelection> selections,
        params ClosureRoot[] roots
    ) =>
        new DependencyClosure(store).ComputeAsync(
            new ClosureRequest(roots, relationships, selections),
            CancellationToken.None
        );

    // Verbatim from the fake's synthetic fixtures — these tests never reach a store, real or fake.
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

    private static ForeignKeyDefinition F(TableDefinition child, TableDefinition parent) =>
        new($"FK_{child.Name}_{parent.Name}", child, parent, ["K1"], ["K1"], true, true);

    private sealed class CountingClosureStore(IClosureStore inner) : IClosureStore, IAsyncDisposable
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

        public Task ResetAsync(CancellationToken cancellationToken) => inner.ResetAsync(cancellationToken);

        public ValueTask DisposeAsync() =>
            inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
