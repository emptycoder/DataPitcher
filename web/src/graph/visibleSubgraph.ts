import { maximumVisibleNodes, type GraphTopology, type VisibleSubgraph } from './model';

export function evaluateExpansion(currentIds: readonly string[], additions: readonly string[]) {
  return new Set([...currentIds, ...additions]).size <= maximumVisibleNodes ? { allowed: true as const }
    : { allowed: false as const, reason: 'Showing more than 200 tables is disabled; focus or collapse a group first. About 400–500 visible simple nodes is the frame-rate soft ceiling.' };
}

export function deriveVisibleSubgraph(topology: GraphTopology, expanded: readonly string[], collapsedSchemas: readonly string[], collapsedComponents: readonly string[]): VisibleSubgraph {
  const visible = new Set(topology.plannedTableIds);
  const queue = [...visible];
  while (queue.length) {
    const childId = queue.shift()!;
    for (const edge of topology.relationships) {
      if (edge.childTableId === childId && !visible.has(edge.parentTableId)) {
        visible.add(edge.parentTableId);
        queue.push(edge.parentTableId);
      }
    }
  }
  for (const id of expanded) {
    for (const edge of topology.relationships) {
      if (edge.childTableId === id || edge.parentTableId === id) {
        visible.add(edge.childTableId);
        visible.add(edge.parentTableId);
      }
    }
  }
  const tables = topology.tables.filter((table) => visible.has(table.id));
  const componentSizes = new Map<string, number>();
  const items = new Map<string, string[]>();
  const itemOf = new Map<string, string>();
  for (const table of tables) componentSizes.set(table.componentId, (componentSizes.get(table.componentId) ?? 0) + 1);
  for (const table of tables) {
    const id = collapsedComponents.includes(table.componentId) && componentSizes.get(table.componentId)! > 1
      ? `scc:${table.componentId}`
      : collapsedSchemas.includes(table.schema)
        ? `schema:${table.schema}`
        : table.id;
    items.set(id, [...(items.get(id) ?? []), table.id]);
    itemOf.set(table.id, id);
  }
  const seen = new Set<string>();
  const relationships = topology.relationships.flatMap((edge) => {
    const childItemId = itemOf.get(edge.childTableId);
    const parentItemId = itemOf.get(edge.parentTableId);
    const key = `${childItemId}|${parentItemId}`;
    return !childItemId || !parentItemId || childItemId === parentItemId || seen.has(key)
      ? []
      : (seen.add(key), [{ id: edge.id, name: edge.name, childItemId, parentItemId }]);
  });
  return {
    tableIds: [...visible].sort(),
    items: [...items].map(([id, memberIds]) => ({ id, memberIds, kind: id.startsWith('schema:') ? 'schema' as const : id.startsWith('scc:') ? 'scc' as const : 'table' as const })).sort((a, b) => a.id.localeCompare(b.id)),
    relationships,
  };
}
