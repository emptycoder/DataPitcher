using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using Xunit;

namespace DataPitcher.UnitTests.Plans;

public sealed class PlanMappingResolverTests
{
    private static readonly SchemaTableAddress Orders = new("dbo", "Orders");

    [Fact]
    public void Resolve_PrefillsEveryReachableTableByNameWithoutRegardToCase()
    {
        var source = Source();
        var target = Snapshot(
            Table("dbo", "orders", ("id", "int", false), ("customerid", "int", false), ("note", "string", true)),
            Table("dbo", "customers", ("id", "int", false), ("name", "string", false))
        );

        var review = PlanMappingResolver.Resolve(source, target, Orders, ["Id"], []);

        Assert.Equal(["Orders", "Customers"], review.Tables.Select(table => table.Source.Name));
        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.Equal(new TableAddress("dbo", "orders"), orders.Target);
        Assert.True(orders.IsRoot);
        Assert.All(orders.Columns, column => Assert.Equal(MappingOrigins.Default, column.Origin));
        Assert.Equal(["id", "customerid", "note"], orders.Columns.Select(column => column.Target));
        Assert.True(orders.Columns.Single(column => column.Source == "Id").IsKey);
        Assert.True(orders.Columns.Single(column => column.Source == "CustomerId").IsForeignKey);
        Assert.False(review.HasBlockers);
        Assert.Empty(review.AllProblems);
    }

    [Fact]
    public void Resolve_WhenASourceColumnHasNoTarget_WarnsThatItsValuesAreDropped()
    {
        var target = Snapshot(
            Table("dbo", "Orders", ("Id", "int", false), ("CustomerId", "int", false)),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], []);

        var note = review.Table(new TableAddress("dbo", "Orders")).Columns.Single(column => column.Source == "Note");
        Assert.Null(note.Target);
        Assert.Equal(MappingOrigins.Unmapped, note.Origin);
        var problem = Assert.Single(note.Problems);
        Assert.Equal("column_unmapped", problem.Code);
        Assert.False(problem.IsBlocker);
        Assert.False(review.HasBlockers);
        Assert.DoesNotContain(
            review.Table(new TableAddress("dbo", "Orders")).ToMapping().Columns,
            column => column.Source == "Note"
        );
    }

    [Fact]
    public void Resolve_WhenAKeyOrForeignKeyColumnHasNoTarget_Blocks()
    {
        var target = Snapshot(
            Table("dbo", "Orders", ("OrderNo", "int", false), ("Note", "string", true)),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], []);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.Equal(
            "key_column_unmapped",
            Assert.Single(orders.Columns.Single(column => column.Source == "Id").Problems).Code
        );
        Assert.Equal(
            "foreign_key_column_unmapped",
            Assert.Single(orders.Columns.Single(column => column.Source == "CustomerId").Problems).Code
        );
        Assert.True(review.HasBlockers);
    }

    [Fact]
    public void Resolve_AppliesOverridesForTableAndColumnsAndVerifiesThemAgainstTheTarget()
    {
        var target = Snapshot(
            Table(
                "sales",
                "OrderHeader",
                ("Id", "int", false),
                ("CustomerId", "int", false),
                ("Remark", "string", true)
            ),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );
        var overrides = new[]
        {
            new TableMappingOverride(
                new TableAddress("dbo", "Orders"),
                new TableAddress("sales", "OrderHeader"),
                [new ColumnMappingOverride("Note", "remark"), new ColumnMappingOverride("Id", "Missing")]
            ),
        };

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], overrides);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.Equal(new TableAddress("sales", "OrderHeader"), orders.Target);
        var note = orders.Columns.Single(column => column.Source == "Note");
        Assert.Equal("Remark", note.Target);
        Assert.Equal(MappingOrigins.Override, note.Origin);
        Assert.Empty(note.Problems);
        var id = orders.Columns.Single(column => column.Source == "Id");
        Assert.Equal("target_column_missing", Assert.Single(id.Problems).Code);
        Assert.True(review.HasBlockers);
    }

    [Fact]
    public void Resolve_WhenTheOperatorExcludesAColumn_LeavesItOutWithoutAWarning()
    {
        var target = Snapshot(
            Table("dbo", "Orders", ("Id", "int", false), ("CustomerId", "int", false), ("Note", "string", true)),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );
        var overrides = new[]
        {
            new TableMappingOverride(
                new TableAddress("dbo", "Orders"),
                null,
                [new ColumnMappingOverride("Note", null)]
            ),
        };

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], overrides);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        var note = orders.Columns.Single(column => column.Source == "Note");
        Assert.Equal(MappingOrigins.Excluded, note.Origin);
        Assert.Empty(note.Problems);
        Assert.Equal(["Id", "CustomerId"], orders.ToMapping().Columns.Select(column => column.Source));
        Assert.Equal("Note", Assert.Single(orders.TargetOnlyColumns).Name);
        Assert.Empty(review.AllProblems);
    }

    [Fact]
    public void Resolve_WhenTwoColumnsAimAtOneTargetColumn_BlocksBoth()
    {
        var target = Snapshot(
            Table("dbo", "Orders", ("Id", "int", false), ("CustomerId", "int", false), ("Note", "string", true)),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );
        var overrides = new[]
        {
            new TableMappingOverride(
                new TableAddress("dbo", "Orders"),
                null,
                [new ColumnMappingOverride("Note", "CustomerId")]
            ),
        };

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], overrides);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.Contains(
            orders.Columns.Single(column => column.Source == "Note").Problems,
            problem => problem.Code == "duplicate_target"
        );
        Assert.Contains(
            orders.Columns.Single(column => column.Source == "CustomerId").Problems,
            problem => problem.Code == "duplicate_target"
        );
        Assert.True(review.HasBlockers);
    }

    [Fact]
    public void Resolve_ReportsTypeAndNullabilityDifferencesAndUnfilledRequiredTargetColumns()
    {
        var target = Snapshot(
            Table(
                "dbo",
                "Orders",
                ("Id", "long", false),
                ("CustomerId", "int", false),
                ("Note", "string", false),
                ("Region", "string", false),
                ("Tag", "string", true)
            ),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false))
        );

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], []);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.Equal(
            "type_mismatch",
            Assert.Single(orders.Columns.Single(column => column.Source == "Id").Problems).Code
        );
        Assert.Equal(
            "nullability_narrowed",
            Assert.Single(orders.Columns.Single(column => column.Source == "Note").Problems).Code
        );
        var region = orders.TargetOnlyColumns.Single(column => column.Name == "Region");
        Assert.Equal("target_required_unfilled", Assert.Single(region.Problems).Code);
        Assert.Empty(orders.TargetOnlyColumns.Single(column => column.Name == "Tag").Problems);
        Assert.False(review.HasBlockers);
        Assert.Equal(3, review.Warnings.Count());
    }

    [Fact]
    public void Resolve_WhenTheTargetSnapshotLacksTheTable_WarnsAndMapsByNameUnchecked()
    {
        var target = Snapshot(Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false)));

        var review = PlanMappingResolver.Resolve(Source(), target, Orders, ["Id"], []);

        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.False(orders.TargetExists);
        var problem = Assert.Single(orders.Problems);
        Assert.Equal("target_table_missing", problem.Code);
        Assert.False(problem.IsBlocker);
        Assert.Equal(["Id", "CustomerId", "Note"], orders.ToMapping().Columns.Select(column => column.Target));
        Assert.False(review.HasBlockers);
    }

    [Fact]
    public void Resolve_WithoutATargetSnapshot_PrefillsByNameAndSaysItIsUnchecked()
    {
        var review = PlanMappingResolver.Resolve(Source(), null, Orders, ["Id"], []);

        Assert.Equal("target_snapshot_missing", Assert.Single(review.Problems).Code);
        Assert.False(review.HasBlockers);
        var orders = review.Table(new TableAddress("dbo", "Orders"));
        Assert.False(orders.TargetExists);
        Assert.Equal(["Id", "CustomerId", "Note"], orders.ToMapping().Columns.Select(column => column.Target));
    }

    private static SchemaSnapshotContent Source() =>
        Snapshot(
            [
                new SchemaForeignKey(
                    "FK_Orders_Customers",
                    Orders,
                    new("dbo", "Customers"),
                    ["CustomerId"],
                    ["Id"],
                    true,
                    true
                ),
                new SchemaForeignKey(
                    "FK_Unrelated",
                    new("dbo", "Audit"),
                    new("dbo", "Orders"),
                    ["OrderId"],
                    ["Id"],
                    true,
                    true
                ),
            ],
            Table("dbo", "Orders", ("Id", "int", false), ("CustomerId", "int", false), ("Note", "string", true)),
            Table("dbo", "Customers", ("Id", "int", false), ("Name", "string", false)),
            Table("dbo", "Audit", ("Id", "int", false), ("OrderId", "int", false))
        );

    private static SchemaSnapshotContent Snapshot(params SchemaTable[] tables) => Snapshot([], tables);

    private static SchemaSnapshotContent Snapshot(SchemaForeignKey[] foreignKeys, params SchemaTable[] tables) =>
        new(tables, foreignKeys, "db");

    private static SchemaTable Table(
        string schema,
        string name,
        params (string Name, string Type, bool Nullable)[] columns
    ) =>
        new(
            schema,
            name,
            columns.Select(column => new SchemaColumn(column.Name, column.Type, column.Type, column.Nullable)),
            new SchemaKey("PK_" + name, [columns[0].Name]),
            []
        );
}
