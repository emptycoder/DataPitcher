import { create } from 'zustand';

export type GraphViewport = Readonly<{ x: number; y: number; zoom: number }>;
export type GraphPosition = Readonly<{ x: number; y: number }>;

type GraphViewState = {
  viewport: GraphViewport;
  focusedTableId: string | null;
  selectedTableIds: ReadonlySet<string>;
  expandedTableIds: ReadonlySet<string>;
  collapsedSchemaIds: ReadonlySet<string>;
  collapsedComponentIds: ReadonlySet<string>;
  pinnedPositions: ReadonlyMap<string, GraphPosition>;
  setViewport: (viewport: GraphViewport) => void;
  setFocusedTableId: (tableId: string | null) => void;
  setGraphTableSelected: (tableId: string, selected: boolean) => void;
  setGraphTableExpanded: (tableId: string, expanded: boolean) => void;
  setSchemaCollapsed: (schemaId: string, collapsed: boolean) => void;
  setComponentCollapsed: (componentId: string, collapsed: boolean) => void;
  setPinnedGraphPosition: (tableId: string, position: GraphPosition) => void;
  clearPinnedGraphPosition: (tableId: string) => void;
};

function updateMembership(values: ReadonlySet<string>, id: string, included: boolean) {
  const next = new Set(values);
  if (included) next.add(id);
  else next.delete(id);
  return next;
}

const useGraphViewState = create<GraphViewState>()((set) => ({
  viewport: { x: 0, y: 0, zoom: 1 },
  focusedTableId: null,
  selectedTableIds: new Set(),
  expandedTableIds: new Set(),
  collapsedSchemaIds: new Set(),
  collapsedComponentIds: new Set(),
  pinnedPositions: new Map(),
  setViewport: (viewport) => set({ viewport }),
  setFocusedTableId: (focusedTableId) => set({ focusedTableId }),
  setGraphTableSelected: (tableId, selected) => set((state) => ({ selectedTableIds: updateMembership(state.selectedTableIds, tableId, selected) })),
  setGraphTableExpanded: (tableId, expanded) => set((state) => ({ expandedTableIds: updateMembership(state.expandedTableIds, tableId, expanded) })),
  setSchemaCollapsed: (schemaId, collapsed) => set((state) => ({ collapsedSchemaIds: updateMembership(state.collapsedSchemaIds, schemaId, collapsed) })),
  setComponentCollapsed: (componentId, collapsed) => set((state) => ({ collapsedComponentIds: updateMembership(state.collapsedComponentIds, componentId, collapsed) })),
  setPinnedGraphPosition: (tableId, position) => set((state) => ({ pinnedPositions: new Map(state.pinnedPositions).set(tableId, position) })),
  clearPinnedGraphPosition: (tableId) => set((state) => {
    const pinnedPositions = new Map(state.pinnedPositions);
    pinnedPositions.delete(tableId);
    return { pinnedPositions };
  }),
}));

export const graphViewActions = {
  setViewport: (viewport: GraphViewport) => useGraphViewState.getState().setViewport(viewport),
  setFocusedTableId: (tableId: string | null) => useGraphViewState.getState().setFocusedTableId(tableId),
  setGraphTableSelected: (tableId: string, selected: boolean) => useGraphViewState.getState().setGraphTableSelected(tableId, selected),
  setGraphTableExpanded: (tableId: string, expanded: boolean) => useGraphViewState.getState().setGraphTableExpanded(tableId, expanded),
  setSchemaCollapsed: (schemaId: string, collapsed: boolean) => useGraphViewState.getState().setSchemaCollapsed(schemaId, collapsed),
  setComponentCollapsed: (componentId: string, collapsed: boolean) => useGraphViewState.getState().setComponentCollapsed(componentId, collapsed),
  setPinnedGraphPosition: (tableId: string, position: GraphPosition) => useGraphViewState.getState().setPinnedGraphPosition(tableId, position),
  clearPinnedGraphPosition: (tableId: string) => useGraphViewState.getState().clearPinnedGraphPosition(tableId),
};
export const useGraphViewport = () => useGraphViewState((state) => state.viewport);
export const useGraphFocusedTableId = () => useGraphViewState((state) => state.focusedTableId);
export const useIsGraphTableSelected = (tableId: string) => useGraphViewState((state) => state.selectedTableIds.has(tableId));
export const useIsGraphTableExpanded = (tableId: string) => useGraphViewState((state) => state.expandedTableIds.has(tableId));
export const useIsSchemaCollapsed = (schemaId: string) => useGraphViewState((state) => state.collapsedSchemaIds.has(schemaId));
export const useIsComponentCollapsed = (componentId: string) => useGraphViewState((state) => state.collapsedComponentIds.has(componentId));
export const usePinnedGraphPosition = (tableId: string) => useGraphViewState((state) => state.pinnedPositions.get(tableId));
