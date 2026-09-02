import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import type { RequestFunction } from '../api/effectivePermissionsApi';
import { planDependencyGraphQueryOptions } from '../api/planDependencyGraphQuery';
import {
  graphViewActions,
  useCollapsedGraphComponentIds,
  useCollapsedGraphSchemaIds,
  useGraphFocusedTableId,
  useGraphPinnedPositions,
  useGraphSelectedTableIds,
  useGraphViewport,
  useGraphExpandedTableIds,
} from '../stores/graphViewStore';
import { DependencyGraphView } from './DependencyGraphView';
import { layoutOptions } from './elkLayout';
import {
  createLayoutCoordinator,
  createLayoutResultCache,
  semanticLayoutKey,
  type LayoutEngine,
  type LayoutResult,
  type LayoutScheduler,
  type MeasuredSizes,
} from './layout';
import type { GraphTopology } from './model';
import { evaluateExpansion, deriveVisibleSubgraph } from './visibleSubgraph';

export type DependencyGraphLayoutAdapter = LayoutEngine & Readonly<{ dispose: () => void }>;
export type DependencyGraphScreenProps = Readonly<{
  planId: string | null;
  request: RequestFunction;
  authentication: AuthenticationAdapter;
  layoutAdapter: DependencyGraphLayoutAdapter;
  cache: ReturnType<typeof createLayoutResultCache>;
  scheduler: LayoutScheduler;
  optionsVersion?: string;
}>;

export function DependencyGraphScreen({
  planId,
  request,
  authentication,
  layoutAdapter,
  cache,
  scheduler,
  optionsVersion = layoutOptions.version,
}: DependencyGraphScreenProps) {
  const query = useQuery({ ...planDependencyGraphQueryOptions(planId ?? '', request, authentication), enabled: planId !== null });
  const focusedItemId = useGraphFocusedTableId();
  const viewport = useGraphViewport();
  const selectedTableIds = useGraphSelectedTableIds();
  const expandedTableIds = useGraphExpandedTableIds();
  const collapsedSchemaIds = useCollapsedGraphSchemaIds();
  const collapsedComponentIds = useCollapsedGraphComponentIds();
  const pinnedPositions = useGraphPinnedPositions();
  const [measuredSizes, setMeasuredSizes] = useState<MeasuredSizes>({});
  const [layout, setLayout] = useState<LayoutResult | null>(null);
  const [relayoutVersion, setRelayoutVersion] = useState(0);
  const [expansionMessage, setExpansionMessage] = useState<string | null>(null);
  const topology = query.data as GraphTopology | undefined;
  const graph = useMemo(
    () => topology && deriveVisibleSubgraph(topology, [...expandedTableIds], [...collapsedSchemaIds], [...collapsedComponentIds]),
    [collapsedComponentIds, collapsedSchemaIds, expandedTableIds, topology],
  );
  const coordinator = useMemo(() => createLayoutCoordinator(layoutAdapter, cache, scheduler), [cache, layoutAdapter, scheduler]);
  const measured = graph?.items.every((item) => measuredSizes[item.id] !== undefined) ?? false;
  const layoutKey = graph && topology && measured
    ? semanticLayoutKey({ revision: topology.revision, visibleItemIds: graph.items.map((item) => item.id), measuredSizes, optionsVersion })
    : null;
  const tableById = useMemo(() => Object.fromEntries((topology?.tables ?? []).map((table) => [table.id, table])), [topology]);

  useEffect(() => () => cache.clear(), [cache, planId]);
  useEffect(() => () => layoutAdapter.dispose(), [layoutAdapter]);
  useEffect(() => {
    setMeasuredSizes({});
    setLayout(null);
  }, [planId]);
  useEffect(() => {
    if (!graph || !layoutKey) return;
    let current = true;
    void coordinator.request(layoutKey, graph, measuredSizes).then((result) => {
      if (current) setLayout(result);
    });
    return () => { current = false; };
  }, [coordinator, graph, layoutKey, measuredSizes, relayoutVersion]);

  const onMeasure = useCallback((itemId: string, size: Readonly<{ width: number; height: number }>) => {
    setMeasuredSizes((sizes) => ({ ...sizes, [itemId]: size }));
  }, []);
  const onExpand = useCallback((tableId: string) => {
    const additions = topology!.relationships.flatMap((relationship) => relationship.childTableId === tableId || relationship.parentTableId === tableId
      ? [relationship.childTableId, relationship.parentTableId]
      : []);
    const evaluation = evaluateExpansion(graph!.tableIds, additions);
    if (evaluation.allowed) {
      graphViewActions.setGraphTableExpanded(tableId, true);
      setExpansionMessage(null);
    } else setExpansionMessage(evaluation.reason);
  }, [graph, topology]);
  const onRelayout = useCallback(() => {
    cache.clear();
    setRelayoutVersion((version) => version + 1);
  }, [cache]);

  if (!planId) return <p role="status">Choose a transfer plan to view its dependencies.</p>;
  if (query.isPending) return <p role="status">Loading dependency graph.</p>;
  if (query.isError || !graph) return <p role="status">Unable to load dependency graph.</p>;

  return (
    <>
      {expansionMessage && <p role="status">{expansionMessage}</p>}
      <DependencyGraphView
        items={graph.items}
        relationships={graph.relationships}
        tableById={tableById}
        positions={layout?.positions ?? {}}
        edgeSections={layout?.edgeSections ?? {}}
        pinnedPositions={Object.fromEntries(pinnedPositions)}
        selectedItemIds={[...selectedTableIds]}
        focusedItemId={focusedItemId}
        onSelect={(itemId) => graphViewActions.setGraphTableSelected(itemId, !selectedTableIds.has(itemId))}
        onFocus={graphViewActions.setFocusedTableId}
        onExpandDependencies={onExpand}
        onExpandDependants={onExpand}
        onViewportChange={graphViewActions.setViewport}
        onPinnedPositionChange={graphViewActions.setPinnedGraphPosition}
        onMeasure={onMeasure}
        onRelayout={onRelayout}
      />
      <output aria-label="Graph viewport">{`${viewport.x},${viewport.y},${viewport.zoom}`}</output>
    </>
  );
}
