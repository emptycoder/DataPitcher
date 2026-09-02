import { memo, useCallback, useMemo } from 'react';
import { ReactFlow, type Edge, type Node, type Viewport } from '@xyflow/react';
import { GraphDetails } from './GraphDetails';
import { GraphLegend } from './GraphLegend';
import { GraphNode, type GraphNodeData } from './GraphNode';
import type { LayoutEdgeSection, LayoutPosition } from './layout';
import type { GraphTable, VisibleItem, VisibleRelationship } from './model';

export type DependencyGraphViewProps = Readonly<{
  items: readonly VisibleItem[];
  relationships: readonly VisibleRelationship[];
  tableById: Readonly<Record<string, GraphTable>>;
  positions: Readonly<Record<string, LayoutPosition>>;
  edgeSections: Readonly<Record<string, readonly LayoutEdgeSection[]>>;
  pinnedPositions: Readonly<Record<string, LayoutPosition | undefined>>;
  selectedItemIds: readonly string[];
  focusedItemId: string | null;
  onSelect: (itemId: string) => void;
  onFocus: (itemId: string) => void;
  onExpandDependencies: (tableId: string) => void;
  onExpandDependants: (tableId: string) => void;
  onViewportChange: (viewport: Readonly<{ x: number; y: number; zoom: number }>) => void;
  onPinnedPositionChange: (itemId: string, position: LayoutPosition) => void;
  onRelayout: () => void;
}>;

type GraphEdgeData = Readonly<{ label: string; sections: readonly LayoutEdgeSection[] }>;
type GraphFlowNode = Node<GraphNodeData, 'dependency'>;
type GraphFlowEdge = Edge<GraphEdgeData, 'dependency'>;

function sectionPath(section: LayoutEdgeSection) {
  return [`M ${section.startPoint.x} ${section.startPoint.y}`, ...section.bendPoints.map((point) => `L ${point.x} ${point.y}`), `L ${section.endPoint.x} ${section.endPoint.y}`].join(' ');
}

function GraphEdge({ id, data }: Readonly<{ id: string; data: GraphEdgeData }>) {
  const markerId = `dependency-arrow-${id}`;
  return (
    <g aria-label={data.label}>
      <defs>
        <marker id={markerId} markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto" aria-hidden="true">
          <path d="M 0 0 L 8 4 L 0 8 z" />
        </marker>
      </defs>
      {data.sections.map((section, index) => <path key={index} d={sectionPath(section)} markerEnd={`url(#${markerId})`} />)}
      <text>{data.label}</text>
    </g>
  );
}

const nodeTypes = { dependency: memo(GraphNode) };
const edgeTypes = { dependency: memo(GraphEdge) };

export function DependencyGraphView({
  items,
  relationships,
  tableById,
  positions,
  edgeSections,
  pinnedPositions,
  selectedItemIds,
  focusedItemId,
  onSelect,
  onFocus,
  onExpandDependencies,
  onExpandDependants,
  onViewportChange,
  onPinnedPositionChange,
  onRelayout,
}: DependencyGraphViewProps) {
  const nodes = useMemo<GraphFlowNode[]>(() => items.map((item) => {
    const table = tableById[item.memberIds[0]!]!;
    return {
      id: item.id,
      type: 'dependency',
      position: pinnedPositions[item.id] ?? positions[item.id]!,
      data: {
        itemId: item.id,
        table,
        selected: selectedItemIds.includes(item.id),
        focused: focusedItemId === item.id,
        parentItemId: relationships.find((relationship) => relationship.childItemId === item.id)?.parentItemId ?? null,
        dependantItemId: relationships.find((relationship) => relationship.parentItemId === item.id)?.childItemId ?? null,
        firstItemId: items[0]!.id,
        lastItemId: items.at(-1)!.id,
        onSelect,
        onFocus,
      },
    };
  }), [focusedItemId, items, onFocus, onSelect, pinnedPositions, positions, relationships, selectedItemIds, tableById]);
  const edges = useMemo<GraphFlowEdge[]>(() => relationships.map((relationship) => {
    const child = tableById[items.find((item) => item.id === relationship.childItemId)!.memberIds[0]!]!;
    const parent = tableById[items.find((item) => item.id === relationship.parentItemId)!.memberIds[0]!]!;
    return {
      id: relationship.id,
      type: 'dependency',
      source: relationship.childItemId,
      target: relationship.parentItemId,
      data: { label: `${child.name} depends on ${parent.name}`, sections: edgeSections[relationship.id] ?? [] },
    };
  }), [edgeSections, items, relationships, tableById]);
  const focusedItem = items.find((item) => item.id === focusedItemId)!;
  const focusedTable = focusedItemId ? tableById[focusedItem.memberIds[0]!]! : null;
  const handleNodeClick = useCallback((_event: unknown, node: GraphFlowNode) => onFocus(node.id), [onFocus]);
  const handleNodeDragStop = useCallback((_event: unknown, node: GraphFlowNode) => onPinnedPositionChange(node.id, node.position), [onPinnedPositionChange]);
  const handleMoveEnd = useCallback((_event: unknown, viewport: Viewport) => onViewportChange(viewport), [onViewportChange]);

  return (
    <section aria-label="Schema dependency graph">
      <GraphLegend />
      <div aria-label="Dependency graph controls">
        <button type="button" onClick={onRelayout}>Relayout</button>
      </div>
      <ReactFlow<GraphFlowNode, GraphFlowEdge>
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onlyRenderVisibleElements
        fitView={false}
        onNodeClick={handleNodeClick}
        onNodeDragStop={handleNodeDragStop}
        onMoveEnd={handleMoveEnd}
      />
      <GraphDetails table={focusedTable} onExpandDependencies={onExpandDependencies} onExpandDependants={onExpandDependants} />
    </section>
  );
}
