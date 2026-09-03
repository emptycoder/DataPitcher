using DataPitcher.Core.Closure;
using DataPitcher.Core.Graph;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Graph;

public sealed class CycleStrategySelectorTests
{
    [Fact]
    public async Task SelectAsync_WhenSelfReferencingRowsAreAForest_ReturnsOrderedBeforeAnyCapability()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var root = R(employees, 1);
        var child = R(employees, 2);
        var request = Request(
            [root, child],
            [
                new(root, manager, null, RowReferenceState.NullParent),
                new(child, manager, root, RowReferenceState.Planned),
            ]
        );
        var analyzer = new ScriptedAnalyzer(("", Ordered(root, child)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(
            request,
            Capabilities(false),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Ordered, result.Strategy);
        Assert.Empty(result.CycleBreakingEdges);
        Assert.Equal([""], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenOrdered_ExposesEveryPlannedRowInInsertionAnalysis()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var root = R(employees, 1);
        var child = R(employees, 2);
        var request = Request(
            [root, child],
            [
                new(root, manager, null, RowReferenceState.NullParent),
                new(child, manager, root, RowReferenceState.Planned),
            ]
        );
        var result = await new CycleStrategySelector(new ScriptedAnalyzer(("", Ordered(root, child)))).SelectAsync(
            request,
            Capabilities(false),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Ordered, result.Strategy);
        Assert.Equal([root, child], result.Analysis.InsertionOrder);
        Assert.Empty(result.Analysis.UnreachedRows);
    }

    [Fact]
    public async Task SelectAsync_WhenSelfReferencingRowsFormAGenuineCycle_UsesPostgreSqlDeferredEdges()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var a = R(employees, 1);
        var b = R(employees, 2);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_Manager", Ordered(a, b)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(
            Request(
                [a, b],
                [new(a, manager, b, RowReferenceState.Planned), new(b, manager, a, RowReferenceState.Planned)]
            ),
            Capabilities(true, new CycleEdgeCapability(manager, true, false, false)),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Deferred, result.Strategy);
        Assert.Equal([manager], result.CycleBreakingEdges);
        Assert.Equal(["", "FK_Manager"], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenDeferredEdgesOrderTheResidualGraph_ExposesResidualAnalysis()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var a = R(employees, 1);
        var b = R(employees, 2);
        var residual = Ordered(b, a);
        var result = await new CycleStrategySelector(
            new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_Manager", residual))
        ).SelectAsync(
            Request(
                [a, b],
                [new(a, manager, b, RowReferenceState.Planned), new(b, manager, a, RowReferenceState.Planned)]
            ),
            Capabilities(true, new CycleEdgeCapability(manager, true, false, false)),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Deferred, result.Strategy);
        Assert.Same(residual, result.Analysis);
        Assert.Equal([b, a], result.Analysis.InsertionOrder);
        Assert.Empty(result.Analysis.UnreachedRows);
    }

    [Fact]
    public async Task SelectAsync_WhenTwoTablesFormACycle_UsesOneSuspendableEdgeSet()
    {
        var aTable = T("A");
        var bTable = T("B");
        var ab = F("FK_A_B", aTable, bTable);
        var ba = F("FK_B_A", bTable, aTable);
        var a = R(aTable, 1);
        var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Ordered(b, a)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(
            Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]),
            Capabilities(false, new CycleEdgeCapability(ab, true, false, true)),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.ConstraintSuspension, result.Strategy);
        Assert.Equal([ab], result.CycleBreakingEdges);
    }

    [Fact]
    public async Task SelectAsync_WhenNullableEdgesBreakEveryCycle_UsesNullableTwoPhase()
    {
        var aTable = T("A");
        var bTable = T("B");
        var ab = F("FK_A_B", aTable, bTable);
        var ba = F("FK_B_A", bTable, aTable);
        var a = R(aTable, 1);
        var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Ordered(b, a)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(
            Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]),
            Capabilities(false, new CycleEdgeCapability(ab, false, true, false)),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.NullableTwoPhase, result.Strategy);
        Assert.Equal([ab], result.CycleBreakingEdges);
    }

    [Fact]
    public async Task SelectAsync_WhenNullableEdgesLeaveAnotherCycle_DoesNotCombineStrategiesAndBlocks()
    {
        var aTable = T("A");
        var bTable = T("B");
        var ab = F("FK_A_B", aTable, bTable);
        var ba = F("FK_B_A", bTable, aTable);
        var bb = F("FK_B_B", bTable, bTable);
        var a = R(aTable, 1);
        var b = R(bTable, 1);
        var analyzer = new ScriptedAnalyzer(("", Cyclic(a, b)), ("FK_A_B", Cyclic(a, b)), ("FK_B_B", Cyclic(a, b)));
        var result = await new CycleStrategySelector(analyzer).SelectAsync(
            Request(
                [a, b],
                [
                    new(a, ab, b, RowReferenceState.Planned),
                    new(b, ba, a, RowReferenceState.Planned),
                    new(b, bb, b, RowReferenceState.Planned),
                ]
            ),
            Capabilities(
                false,
                new CycleEdgeCapability(ab, false, true, false),
                new CycleEdgeCapability(bb, false, false, true)
            ),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Blocked, result.Strategy);
        Assert.True(result.MustBlockScc);
        Assert.Equal(["", "FK_A_B", "FK_B_B"], analyzer.Calls);
    }

    [Fact]
    public async Task SelectAsync_WhenNoEligibleEdgeSetBreaksTheCycle_BlocksWithTablesAndRelationships()
    {
        var alpha = T("Alpha");
        var beta = T("Beta");
        var ab = F("FK_Alpha_Beta", alpha, beta);
        var ba = F("FK_Beta_Alpha", beta, alpha);
        var a = R(alpha, 1);
        var b = R(beta, 1);
        var result = await new CycleStrategySelector(new ScriptedAnalyzer(("", Cyclic(a, b)))).SelectAsync(
            Request([a, b], [new(a, ab, b, RowReferenceState.Planned), new(b, ba, a, RowReferenceState.Planned)]),
            Capabilities(false),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Blocked, result.Strategy);
        Assert.Contains("dbo.Alpha", result.Explanation);
        Assert.Contains("dbo.Beta", result.Explanation);
        Assert.Contains("FK_Alpha_Beta", result.Explanation);
        Assert.Contains("FK_Beta_Alpha", result.Explanation);
    }

    [Fact]
    public async Task SelectAsync_WhenBlocked_ExposesTheUnreachedRows()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var a = R(employees, 1);
        var b = R(employees, 2);
        var result = await new CycleStrategySelector(new ScriptedAnalyzer(("", Cyclic(a, b)))).SelectAsync(
            Request(
                [a, b],
                [new(a, manager, b, RowReferenceState.Planned), new(b, manager, a, RowReferenceState.Planned)]
            ),
            Capabilities(false),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Blocked, result.Strategy);
        Assert.Empty(result.Analysis.InsertionOrder);
        Assert.Equal([a, b], result.Analysis.UnreachedRows);
    }

    [Fact]
    public async Task SelectAsync_WhenExternalParentIsMissing_ReportsItWithoutClassifyingACycle()
    {
        var employees = T("Employees");
        var manager = F("FK_Manager", employees, employees);
        var employee = R(employees, 2);
        var absentManager = R(employees, 99);
        var missing = new MissingReference(employee, manager, absentManager);
        var analysis = new RowGraphAnalysis([employee], [], [missing]);
        var result = await new CycleStrategySelector(new ScriptedAnalyzer(("", analysis))).SelectAsync(
            Request([employee], [new(employee, manager, absentManager, RowReferenceState.Missing)]),
            Capabilities(false),
            CancellationToken.None
        );
        Assert.Equal(CycleStrategy.Ordered, result.Strategy);
        Assert.Empty(result.Analysis.UnreachedRows);
        Assert.Equal(missing, Assert.Single(result.Analysis.MissingReferences));
    }

    private static TableDefinition T(string name) => new("dbo", name, [], new($"PK_{name}", ["Id"]), []);

    private static ForeignKeyDefinition F(string name, TableDefinition child, TableDefinition parent) =>
        new(name, child, parent, ["RefId"], ["Id"], true, true);

    private static RowAddress R(TableDefinition table, int id) => new(table, new StableKey([new("Id", id)]));

    private static RowGraphRequest Request(RowAddress[] rows, RowReference[] references) =>
        new(rows, references, new(10, TimeSpan.FromSeconds(5), 1_000_000));

    private static RowGraphAnalysis Ordered(params RowAddress[] rows) => new(rows, [], []);

    private static RowGraphAnalysis Cyclic(params RowAddress[] rows) => new([], rows, []);

    private static CycleStrategyCapabilities Capabilities(bool supportsDeferred, params CycleEdgeCapability[] edges) =>
        new(supportsDeferred, edges);

    private sealed class ScriptedAnalyzer(params (string Excluded, RowGraphAnalysis Analysis)[] answers)
        : ISetBasedRowGraphAnalyzer
    {
        private readonly Dictionary<string, RowGraphAnalysis> _answers = answers.ToDictionary(
            x => x.Excluded,
            x => x.Analysis
        );
        public List<string> Calls { get; } = [];

        public Task<RowGraphAnalysis> AnalyzeAsync(
            RowGraphRequest request,
            IReadOnlyCollection<ForeignKeyDefinition> excludedRelationships,
            CancellationToken cancellationToken
        )
        {
            var key = string.Join(
                ",",
                excludedRelationships.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            );
            Calls.Add(key);
            return Task.FromResult(_answers[key]);
        }
    }
}
