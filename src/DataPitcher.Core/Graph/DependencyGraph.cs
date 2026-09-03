using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public sealed class DependencyGraph
{
    private readonly IReadOnlyDictionary<TableDefinition, IReadOnlyList<ForeignKeyDefinition>> _dependencies;
    private readonly IReadOnlyDictionary<TableDefinition, IReadOnlyList<ForeignKeyDefinition>> _dependents;

    public DependencyGraph(IEnumerable<TableDefinition> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
    {
        var tableList = tables.OrderBy(QualifiedName, StringComparer.Ordinal).ToArray();
        var canonicalTables = tableList.ToDictionary(x => x);
        var dependencies = tableList.ToDictionary(x => x, _ => new List<ForeignKeyDefinition>());
        var dependents = tableList.ToDictionary(x => x, _ => new List<ForeignKeyDefinition>());

        foreach (var foreignKey in foreignKeys)
        {
            var childTable = canonicalTables[foreignKey.ChildTable];
            var parentTable = canonicalTables[foreignKey.ParentTable];
            var canonicalForeignKey = new ForeignKeyDefinition(
                foreignKey.Name,
                childTable,
                parentTable,
                foreignKey.ChildColumns,
                foreignKey.ParentColumns,
                foreignKey.IsEnforced,
                foreignKey.IsTrusted
            );
            dependencies[childTable].Add(canonicalForeignKey);
            dependents[parentTable].Add(canonicalForeignKey);
        }

        Tables = Array.AsReadOnly(tableList);
        _dependencies = dependencies.ToDictionary(
            x => x.Key,
            x =>
                (IReadOnlyList<ForeignKeyDefinition>)
                    Array.AsReadOnly(
                        x.Value.OrderBy(foreignKey => QualifiedName(foreignKey.ParentTable), StringComparer.Ordinal)
                            .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                            .ToArray()
                    )
        );
        _dependents = dependents.ToDictionary(
            x => x.Key,
            x =>
                (IReadOnlyList<ForeignKeyDefinition>)
                    Array.AsReadOnly(
                        x.Value.OrderBy(foreignKey => QualifiedName(foreignKey.ChildTable), StringComparer.Ordinal)
                            .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                            .ToArray()
                    )
        );
    }

    public IReadOnlyList<TableDefinition> Tables { get; }

    public IReadOnlyList<ForeignKeyDefinition> DependenciesOf(TableDefinition table) => _dependencies[table];

    public IReadOnlyList<ForeignKeyDefinition> DependentsOf(TableDefinition table) => _dependents[table];

    private static string QualifiedName(TableDefinition table) => $"{table.Schema}.{table.Name}";
}
