import ELK, { type ElkNode } from 'elkjs/lib/elk-api.js';
import ElkWorker from 'elkjs/lib/elk-worker.min.js?worker';
import type { LayoutEngine, LayoutResult, MeasuredSizes } from './layout';
import type { VisibleSubgraph } from './model';

export const layoutOptions = {
  version: 'dependency-graph-v1',
  'elk.algorithm': 'layered',
  'elk.direction': 'RIGHT',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.spacing.nodeNode': '40',
  'elk.layered.spacing.nodeNodeBetweenLayers': '80',
  'elk.layered.cycleBreaking.strategy': 'GREEDY',
} as const;

export function toElkGraph(graph: VisibleSubgraph, sizes: MeasuredSizes): ElkNode {
  return {
    id: 'root',
    layoutOptions,
    children: graph.items.map((item) => ({ id: item.id, width: sizes[item.id]!.width, height: sizes[item.id]!.height })),
    edges: graph.relationships.map((edge) => ({ id: edge.id, sources: [edge.childItemId], targets: [edge.parentItemId] })),
  };
}

export function fromElkLayout(key: string, result: ElkNode): LayoutResult {
  return {
    key,
    positions: Object.fromEntries((result.children ?? []).map((child) => [child.id, { x: child.x!, y: child.y! }])),
    edgeSections: Object.fromEntries((result.edges ?? []).map((edge) => [edge.id, (edge.sections ?? []).map((section) => ({
      startPoint: section.startPoint,
      bendPoints: section.bendPoints ?? [],
      endPoint: section.endPoint,
    }))])),
  };
}

export function createElkLayoutAdapter(): LayoutEngine & Readonly<{ dispose: () => void }> {
  const elk = new ELK({ workerFactory: () => new ElkWorker() });
  return {
    layout: async (key, graph, sizes) => fromElkLayout(key, await elk.layout(toElkGraph(graph, sizes))),
    dispose: () => elk.terminateWorker(),
  };
}
