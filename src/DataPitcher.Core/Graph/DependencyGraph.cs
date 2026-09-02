using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Graph;

public sealed class DependencyGraph
{
    private readonly IReadOnlyDictionary<TableDefinition, IReadOnlyList<ForeignKeyDefinition>> _dependencies;
    private readonly IReadOnlyDictionary<TableDefinition, IReadOnlyList<ForeignKeyDefinition>> _dependents;

    public DependencyGraph(IEnumerable<TableDefinition> tables, IEnumerable<ForeignKeyDefinition> foreignKeys)
    {
        var tableList = tables.Distinct().ToArray();
        var dependencies = tableList.ToDictionary(x => x, _ => new List<ForeignKeyDefinition>());
        var dependents = tableList.ToDictionary(x => x, _ => new List<ForeignKeyDefinition>());

        foreach (var foreignKey in foreignKeys.ToArray())
        {
            dependencies[foreignKey.ChildTable].Add(foreignKey);
            dependents[foreignKey.ParentTable].Add(foreignKey);
        }

        Tables = Array.AsReadOnly(tableList);
        _dependencies = dependencies.ToDictionary(x => x.Key, x => (IReadOnlyList<ForeignKeyDefinition>)Array.AsReadOnly(x.Value.ToArray()));
        _dependents = dependents.ToDictionary(x => x.Key, x => (IReadOnlyList<ForeignKeyDefinition>)Array.AsReadOnly(x.Value.ToArray()));
    }

    public IReadOnlyList<TableDefinition> Tables { get; }

    public IReadOnlyList<ForeignKeyDefinition> DependenciesOf(TableDefinition table) => _dependencies[table];

    public IReadOnlyList<ForeignKeyDefinition> DependentsOf(TableDefinition table) => _dependents[table];
}
