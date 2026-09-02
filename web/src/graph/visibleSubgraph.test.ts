import { expect, it } from 'vitest';
import type { GraphTopology } from './model';
import { deriveVisibleSubgraph, evaluateExpansion } from './visibleSubgraph';

const topology: GraphTopology = { revision: 'r1', plannedTableIds: ['orders'], tables: [
  { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders-customers', state: 'root-selected' },
  { id: 'customers', schema: 'sales', name: 'customers', componentId: 'orders-customers', state: 'required-dependency' },
  { id: 'order-lines', schema: 'sales', name: 'order-lines', componentId: 'lines', state: 'unselected' },
  { id: 'products', schema: 'inventory', name: 'products', componentId: 'products', state: 'unselected' },
  { id: 'categories', schema: 'inventory', name: 'categories', componentId: 'categories', state: 'unselected' },
], relationships: [
  { id: 'orders-customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' },
  { id: 'lines-orders', name: 'FK_lines_orders', childTableId: 'order-lines', parentTableId: 'orders' },
  { id: 'lines-orders-alias', name: 'FK_lines_orders_alias', childTableId: 'order-lines', parentTableId: 'orders' },
  { id: 'products-categories', name: 'FK_products_categories', childTableId: 'products', parentTableId: 'categories' },
] };

it('excludes inbound dependants until an operator expands orders', () => {
  expect(deriveVisibleSubgraph(topology, [], [], []).tableIds).toEqual(['customers', 'orders']);
  expect(deriveVisibleSubgraph(topology, ['orders'], [], []).tableIds).toEqual(['customers', 'order-lines', 'orders']);
});

it('refuses the 201st simultaneously visible table', () => {
  expect(evaluateExpansion(['orders'], ['orders']).allowed).toBe(true);
  expect(evaluateExpansion(Array.from({ length: 200 }, (_, i) => `t${i}`), ['one-more']).allowed).toBe(false);
});

it('collapses multi-table components after explicit neighbour expansion', () => {
  const subgraph = deriveVisibleSubgraph(topology, ['orders'], [], ['orders-customers']);
  expect(subgraph.items).toEqual([
    { id: 'order-lines', kind: 'table', memberIds: ['order-lines'] },
    { id: 'scc:orders-customers', kind: 'scc', memberIds: ['orders', 'customers'] },
  ]);
  expect(subgraph.relationships).toEqual([{ id: 'lines-orders', name: 'FK_lines_orders', childItemId: 'order-lines', parentItemId: 'scc:orders-customers' }]);
});

it('collapses schemas and leaves a single-table component as a table', () => {
  expect(deriveVisibleSubgraph(topology, ['orders'], ['sales'], []).items).toEqual([
    { id: 'schema:sales', kind: 'schema', memberIds: ['orders', 'customers', 'order-lines'] },
  ]);
  expect(deriveVisibleSubgraph(topology, [], [], ['lines']).items.map((item) => item.kind)).toEqual(['table', 'table']);
});
