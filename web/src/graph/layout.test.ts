import { expect, it, vi } from 'vitest';
import type { VisibleSubgraph } from './model';
import {
  createLayoutCoordinator,
  createLayoutResultCache,
  semanticLayoutKey,
  type LayoutEngine,
  type LayoutResult,
  type LayoutScheduler,
} from './layout';

const graph: VisibleSubgraph = {
  items: [
    { id: 'customers', kind: 'table', memberIds: ['customers'] },
    { id: 'orders', kind: 'table', memberIds: ['orders'] },
  ],
  relationships: [{ id: 'orders-customers', name: 'FK_orders_customers', childItemId: 'orders', parentItemId: 'customers' }],
  tableIds: ['customers', 'orders'],
};
const sizes = { customers: { width: 120, height: 40 }, orders: { width: 100, height: 40 } };
const semanticInputs = {
  revision: 'r1',
  visibleItemIds: ['orders', 'customers'],
  measuredSizes: sizes,
  optionsVersion: 'v1',
};

it('keys only semantic layout inputs despite new identities and interaction changes', () => {
  const key = semanticLayoutKey(semanticInputs);
  const equivalentWithInteraction = {
    ...semanticInputs,
    visibleItemIds: ['customers', 'orders'],
    measuredSizes: { orders: { width: 100, height: 40 }, customers: { width: 120, height: 40 } },
    viewport: { x: 10, y: 20, zoom: 2 },
    focusedTableId: 'orders',
    highlightedTableIds: ['customers'],
    theme: 'dark',
    transferProgress: 50,
    pinnedPosition: { x: 400, y: 300 },
  };

  expect(semanticLayoutKey(equivalentWithInteraction)).toBe(key);
  expect(semanticLayoutKey({ ...semanticInputs, revision: 'r2' })).not.toBe(key);
  expect(semanticLayoutKey({ ...semanticInputs, visibleItemIds: ['orders'] })).not.toBe(key);
  expect(semanticLayoutKey({ ...semanticInputs, measuredSizes: { ...sizes, orders: { width: 101, height: 40 } } })).not.toBe(key);
  expect(semanticLayoutKey({ ...semanticInputs, optionsVersion: 'v2' })).not.toBe(key);
});

it('caches layout results by semantic key and clears them', () => {
  const cache = createLayoutResultCache();
  const result: LayoutResult = { key: 'key', positions: { orders: { x: 1, y: 2 } }, edgeSections: {} };

  expect(cache.get('key')).toBeUndefined();
  cache.set(result);
  expect(cache.get('key')).toBe(result);
  cache.clear();
  expect(cache.get('key')).toBeUndefined();
});

it('runs the engine through the scheduler only for a cache miss', async () => {
  const result: LayoutResult = { key: 'key', positions: { orders: { x: 1, y: 2 } }, edgeSections: {} };
  const engine: LayoutEngine = { layout: vi.fn(async () => result) };
  const scheduler: LayoutScheduler = vi.fn(async (work) => work());
  const coordinator = createLayoutCoordinator(engine, createLayoutResultCache(), scheduler);

  await expect(coordinator.request('key', graph, sizes)).resolves.toBe(result);
  await expect(coordinator.request('key', { ...graph, items: [...graph.items] }, { ...sizes })).resolves.toBe(result);
  expect(scheduler).toHaveBeenCalledOnce();
  expect(engine.layout).toHaveBeenCalledOnce();
  expect(engine.layout).toHaveBeenCalledWith('key', graph, sizes);
});

it('rejects a stale engine result without caching it', async () => {
  const cache = createLayoutResultCache();
  const engine: LayoutEngine = { layout: vi.fn(async () => ({ key: 'stale', positions: {}, edgeSections: {} })) };
  const scheduler: LayoutScheduler = async (work) => work();
  const coordinator = createLayoutCoordinator(engine, cache, scheduler);

  await expect(coordinator.request('current', graph, sizes)).rejects.toThrow('Layout result key does not match the request.');
  expect(cache.get('current')).toBeUndefined();
});
