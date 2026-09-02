import type { VisibleSubgraph } from './model';

export type LayoutPosition = Readonly<{ x: number; y: number }>;
export type LayoutEdgeSection = Readonly<{ startPoint: LayoutPosition; bendPoints: readonly LayoutPosition[]; endPoint: LayoutPosition }>;
export type LayoutResult = Readonly<{
  key: string;
  positions: Readonly<Record<string, LayoutPosition>>;
  edgeSections: Readonly<Record<string, readonly LayoutEdgeSection[]>>;
}>;
export type MeasuredSizes = Readonly<Record<string, Readonly<{ width: number; height: number }>>>;
export type LayoutEngine = Readonly<{
  layout: (key: string, graph: VisibleSubgraph, sizes: MeasuredSizes) => Promise<LayoutResult>;
}>;
export type LayoutScheduler = <Value>(work: () => Promise<Value>) => Promise<Value>;
export type LayoutCoordinator = Readonly<{
  request: (key: string, graph: VisibleSubgraph, sizes: MeasuredSizes) => Promise<LayoutResult>;
}>;

export function semanticLayoutKey({ revision, visibleItemIds, measuredSizes, optionsVersion }: Readonly<{
  revision: string;
  visibleItemIds: readonly string[];
  measuredSizes: MeasuredSizes;
  optionsVersion: string;
}>): string {
  return JSON.stringify({
    revision,
    visibleItemIds: [...visibleItemIds].sort(),
    measuredSizes: Object.entries(measuredSizes)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([id, { width, height }]) => [id, width, height]),
    optionsVersion,
  });
}

export function createLayoutResultCache() {
  const results = new Map<string, LayoutResult>();
  return {
    get: (key: string) => results.get(key),
    set: (result: LayoutResult) => results.set(result.key, result),
    clear: () => results.clear(),
  };
}

export function createLayoutCoordinator(engine: LayoutEngine, cache: ReturnType<typeof createLayoutResultCache>, scheduler: LayoutScheduler): LayoutCoordinator {
  return {
    request: async (key, graph, sizes) => {
      const cached = cache.get(key);
      if (cached) return cached;
      const result = await scheduler(() => engine.layout(key, graph, sizes));
      if (result.key !== key) throw new Error('Layout result key does not match the request.');
      cache.set(result);
      return result;
    },
  };
}
