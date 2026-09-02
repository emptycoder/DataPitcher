import { beforeEach, expect, it, vi } from 'vitest';
import type { VisibleSubgraph } from './model';

const mocks = vi.hoisted(() => ({
  layout: vi.fn(),
  terminateWorker: vi.fn(),
  worker: vi.fn(function Worker() {}),
}));

vi.mock('elkjs/lib/elk-api.js', () => ({
  default: function ELK({ workerFactory }: { workerFactory: () => unknown }) {
    workerFactory();
    return { layout: mocks.layout, terminateWorker: mocks.terminateWorker };
  },
}));
vi.mock('elkjs/lib/elk-worker.min.js?worker', () => ({ default: mocks.worker }));

import { createElkLayoutAdapter, fromElkLayout, layoutOptions, toElkGraph } from './elkLayout';

const graph: VisibleSubgraph = {
  items: [
    { id: 'orders', kind: 'table', memberIds: ['orders'] },
    { id: 'customers', kind: 'table', memberIds: ['customers'] },
  ],
  relationships: [{ id: 'orders-customers', name: 'FK_orders_customers', childItemId: 'orders', parentItemId: 'customers' }],
  tableIds: ['customers', 'orders'],
};
const sizes = { orders: { width: 100, height: 40 }, customers: { width: 120, height: 40 } };
const laidOutGraph = {
  id: 'root',
  children: [{ id: 'orders', x: 10, y: 20 }, { id: 'customers', x: 200, y: 20 }],
  edges: [{
    id: 'orders-customers', sources: ['orders'], targets: ['customers'], sections: [{
      id: 'section-1', startPoint: { x: 110, y: 40 }, bendPoints: [{ x: 155, y: 40 }], endPoint: { x: 200, y: 40 },
    }],
  }],
};

beforeEach(() => {
  vi.clearAllMocks();
  mocks.layout.mockResolvedValue(laidOutGraph);
});

it('converts child-to-parent relationships into a rightward layered ELK graph', () => {
  expect(layoutOptions).toEqual({
    version: 'dependency-graph-v1',
    'elk.algorithm': 'layered',
    'elk.direction': 'RIGHT',
    'elk.edgeRouting': 'ORTHOGONAL',
    'elk.spacing.nodeNode': '40',
    'elk.layered.spacing.nodeNodeBetweenLayers': '80',
    'elk.layered.cycleBreaking.strategy': 'GREEDY',
  });
  expect(toElkGraph(graph, sizes)).toEqual({
    id: 'root',
    layoutOptions,
    children: [
      { id: 'orders', width: 100, height: 40 },
      { id: 'customers', width: 120, height: 40 },
    ],
    edges: [{ id: 'orders-customers', sources: ['orders'], targets: ['customers'] }],
  });
});

it('maps ELK coordinates and edge sections into a keyed layout result', () => {
  expect(fromElkLayout('key', laidOutGraph)).toEqual({
    key: 'key',
    positions: { orders: { x: 10, y: 20 }, customers: { x: 200, y: 20 } },
    edgeSections: {
      'orders-customers': [{
        startPoint: { x: 110, y: 40 }, bendPoints: [{ x: 155, y: 40 }], endPoint: { x: 200, y: 40 },
      }],
    },
  });
  expect(fromElkLayout('empty', { id: 'root' })).toEqual({ key: 'empty', positions: {}, edgeSections: {} });
  expect(fromElkLayout('no-sections', { id: 'root', edges: [{ id: 'edge', sources: [], targets: [] }] }))
    .toEqual({ key: 'no-sections', positions: {}, edgeSections: { edge: [] } });
  expect(fromElkLayout('straight', {
    id: 'root', edges: [{ id: 'edge', sources: [], targets: [], sections: [{ id: 'section', startPoint: { x: 1, y: 2 }, endPoint: { x: 3, y: 4 } }] }],
  })).toEqual({
    key: 'straight', positions: {}, edgeSections: { edge: [{ startPoint: { x: 1, y: 2 }, bendPoints: [], endPoint: { x: 3, y: 4 } }] },
  });
});

it('uses the minified worker factory and terminates it on disposal', async () => {
  const adapter = createElkLayoutAdapter();

  await expect(adapter.layout('key', graph, sizes)).resolves.toEqual(fromElkLayout('key', laidOutGraph));
  expect(mocks.worker).toHaveBeenCalledOnce();
  expect(mocks.layout).toHaveBeenCalledWith(toElkGraph(graph, sizes));
  adapter.dispose();
  expect(mocks.terminateWorker).toHaveBeenCalledOnce();
});
