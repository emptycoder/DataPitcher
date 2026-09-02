import type { ComponentType } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { LayoutEngine, LayoutResult, LayoutScheduler } from './layout';
import { createLayoutCoordinator, createLayoutResultCache } from './layout';
import type { GraphTable, VisibleSubgraph } from './model';

type CapturedFlowProps = Readonly<{
  nodes: readonly Readonly<{ id: string; position: Readonly<{ x: number; y: number }>; data: unknown }>[];
  edges: readonly Readonly<{ id: string; data: unknown }>[];
  nodeTypes: Readonly<Record<string, ComponentType<Readonly<{ id: string; data: unknown }>>>>;
  edgeTypes: Readonly<Record<string, ComponentType<Readonly<{ id: string; data: unknown }>>>>;
  onlyRenderVisibleElements: boolean;
  fitView: boolean;
  onNodeClick: (event: unknown, node: Readonly<{ id: string }>) => void;
  onNodeDragStop: (event: unknown, node: Readonly<{ id: string; position: Readonly<{ x: number; y: number }> }>) => void;
  onMoveEnd: (event: unknown, viewport: Readonly<{ x: number; y: number; zoom: number }>) => void;
  onNodesChange: (changes: readonly unknown[]) => void;
}>;

const flow = vi.hoisted(() => ({ props: undefined as unknown }));

vi.mock('@xyflow/react', async () => {
  const { createElement } = await import('react');
  return {
    ReactFlow: (props: unknown) => {
      const captured = props as CapturedFlowProps;
      flow.props = captured;
      return createElement('div', { 'data-testid': 'react-flow' }, [
        createElement('svg', { key: 'edges' }, captured.edges.map((edge) => createElement(captured.edgeTypes.dependency!, { key: edge.id, id: edge.id, data: edge.data }))),
        ...captured.nodes.map((node) => createElement(captured.nodeTypes.dependency!, { key: node.id, id: node.id, data: node.data })),
      ]);
    },
  };
});

import { DependencyGraphView } from './DependencyGraphView';

afterEach(cleanup);

const tables = [
  { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'root-selected' },
  { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'required-dependency' },
  { id: 'order-lines', schema: 'sales', name: 'order-lines', componentId: 'order-lines', state: 'explicit-dependent' },
  { id: 'archive', schema: 'sales', name: 'archive', componentId: 'archive', state: 'target-satisfied' },
  { id: 'blocked', schema: 'sales', name: 'blocked', componentId: 'blocked', state: 'blocked' },
  { id: 'conflict', schema: 'sales', name: 'conflict', componentId: 'conflict', state: 'conflict' },
  { id: 'cycle', schema: 'sales', name: 'cycle', componentId: 'cycle', state: 'cycle-member' },
  { id: 'unselected', schema: 'sales', name: 'unselected', componentId: 'unselected', state: 'unselected' },
] satisfies readonly GraphTable[];

const graph: VisibleSubgraph = {
  items: tables.map((table) => ({ id: table.id, kind: 'table' as const, memberIds: [table.id] })),
  relationships: [
    { id: 'orders-customers', name: 'FK_orders_customers', childItemId: 'orders', parentItemId: 'customers' },
    { id: 'lines-orders', name: 'FK_lines_orders', childItemId: 'order-lines', parentItemId: 'orders' },
  ],
  tableIds: tables.map((table) => table.id),
};

const tableById: Readonly<Record<string, GraphTable>> = Object.fromEntries(tables.map((table) => [table.id, table]));
const positions = Object.fromEntries(tables.map((table, index) => [table.id, { x: index * 100, y: 0 }]));
const edgeSections = {
  'orders-customers': [{ startPoint: { x: 100, y: 20 }, bendPoints: [{ x: 150, y: 20 }], endPoint: { x: 200, y: 20 } }, { startPoint: { x: 200, y: 20 }, bendPoints: [], endPoint: { x: 220, y: 20 } }],
};

function capturedFlow() {
  return flow.props as CapturedFlowProps;
}

function viewProps(overrides: Partial<React.ComponentProps<typeof DependencyGraphView>> = {}) {
  return {
    items: graph.items,
    relationships: graph.relationships,
    tableById,
    positions,
    edgeSections,
    pinnedPositions: { orders: { x: 300, y: 400 } },
    selectedItemIds: ['orders'],
    focusedItemId: 'orders',
    onSelect: vi.fn(),
    onFocus: vi.fn(),
    onExpandDependencies: vi.fn(),
    onExpandDependants: vi.fn(),
    onViewportChange: vi.fn(),
    onPinnedPositionChange: vi.fn(),
    onMeasure: vi.fn(),
    onRelayout: vi.fn(),
    ...overrides,
  };
}

it('renders text, icons, badges, and borders for every state and states child-to-parent direction', () => {
  render(<DependencyGraphView {...viewProps()} />);

  expect(screen.getByText('Child — depends on → Parent')).toBeVisible();
  expect(screen.getByText('The arrow points from child to required parent.')).toBeVisible();
  expect(screen.getByLabelText('orders depends on customers')).toBeVisible();
  expect(screen.getByText('orders depends on customers')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Details for sales.orders' })).toBeVisible();
  expect(screen.getByText('Expanding dependants only reveals schema context and does not select or transfer those rows.')).toBeVisible();

  const states: readonly (readonly [string, string, string])[] = [
    ['○', 'Unselected', 'unselected'], ['●', 'Root selected', 'orders'], ['↗', 'Required dependency', 'customers'], ['↘', 'Explicit dependent', 'order-lines'],
    ['✓', 'Target satisfied', 'archive'], ['!', 'Blocked', 'blocked'], ['⚠', 'Conflict', 'conflict'], ['⟲', 'Cycle member', 'cycle'],
  ];
  for (const [icon, label, name] of states) {
    expect(screen.getAllByText(icon)[0]!).toBeVisible();
    expect(screen.getByRole('button', { name: new RegExp(`${label} sales\\.${name}`) })).toHaveClass('border');
  }
});

it('selects nodes and moves keyboard focus from child to parent and back to a dependant', () => {
  const onSelect = vi.fn();
  const onFocus = vi.fn();
  const { rerender } = render(<DependencyGraphView {...viewProps({ onSelect, onFocus })} />);
  const orders = screen.getByRole('button', { name: /Root selected sales\.orders/ });

  fireEvent.click(orders);
  fireEvent.keyDown(orders, { key: 'Enter' });
  fireEvent.keyDown(orders, { key: ' ' });
  fireEvent.keyDown(orders, { key: 'ArrowRight' });
  fireEvent.keyDown(orders, { key: 'ArrowLeft' });
  fireEvent.keyDown(orders, { key: 'Home' });
  fireEvent.keyDown(orders, { key: 'End' });
  fireEvent.keyDown(orders, { key: 'Escape' });
  expect(onSelect).toHaveBeenCalledTimes(3);
  expect(onFocus).toHaveBeenCalledWith('customers');
  expect(onFocus).toHaveBeenCalledWith('order-lines');

  rerender(<DependencyGraphView {...viewProps({ focusedItemId: 'customers', onSelect, onFocus })} />);
  expect(screen.getByRole('button', { name: /Required dependency sales\.customers/ })).toHaveFocus();
  fireEvent.keyDown(screen.getByRole('button', { name: /Required dependency sales\.customers/ }), { key: 'ArrowRight' });
  expect(onFocus).toHaveBeenCalledTimes(4);
});

it('enables culling and forwards graph interaction callbacks without relayout', () => {
  const onFocus = vi.fn();
  const onExpandDependencies = vi.fn();
  const onExpandDependants = vi.fn();
  const onViewportChange = vi.fn();
  const onPinnedPositionChange = vi.fn();
  const onMeasure = vi.fn();
  const onRelayout = vi.fn();
  render(<DependencyGraphView {...viewProps({ positions: {}, onFocus, onExpandDependencies, onExpandDependants, onViewportChange, onPinnedPositionChange, onMeasure, onRelayout })} />);

  expect(capturedFlow().onlyRenderVisibleElements).toBe(true);
  expect(capturedFlow().fitView).toBe(false);
  expect(capturedFlow().nodes.find((node) => node.id === 'orders')?.position).toEqual({ x: 300, y: 400 });
  expect(capturedFlow().nodes.find((node) => node.id === 'customers')?.position).toEqual({ x: 0, y: 0 });
  act(() => {
    capturedFlow().onNodeClick({}, { id: 'customers' });
    capturedFlow().onNodeDragStop({}, { id: 'orders', position: { x: 320, y: 420 } });
    capturedFlow().onMoveEnd({}, { x: 10, y: 20, zoom: 2 });
    capturedFlow().onNodesChange([{ id: 'orders', type: 'position' }, { id: 'orders', type: 'dimensions', dimensions: { width: 200, height: 80 } }]);
  });
  fireEvent.mouseEnter(screen.getByTestId('react-flow'));
  fireEvent.focus(screen.getByRole('button', { name: /Root selected sales\.orders/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Expand dependencies' }));
  fireEvent.click(screen.getByRole('button', { name: 'Expand dependants' }));

  expect(onFocus).toHaveBeenCalledWith('customers');
  expect(onExpandDependencies).toHaveBeenCalledWith('orders');
  expect(onExpandDependants).toHaveBeenCalledWith('orders');
  expect(onPinnedPositionChange).toHaveBeenCalledWith('orders', { x: 320, y: 420 });
  expect(onViewportChange).toHaveBeenCalledWith({ x: 10, y: 20, zoom: 2 });
  expect(onMeasure).toHaveBeenCalledWith('orders', { width: 200, height: 80 });
  expect(onRelayout).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: 'Relayout' })).toBeVisible();
});

it('does not invoke the scheduler or engine during a React re-render and invokes them only for Relayout', async () => {
  const engine = {
    layout: vi.fn(async (key: string): Promise<LayoutResult> => ({ key, positions: {}, edgeSections: {} })),
  } satisfies LayoutEngine;
  const scheduler = vi.fn((work: () => Promise<LayoutResult>) => work()) as unknown as LayoutScheduler;
  const coordinator = createLayoutCoordinator(engine, createLayoutResultCache(), scheduler);
  const onRelayout = () => coordinator.request('explicit', graph, {});
  const props = viewProps({ onRelayout });
  const { rerender } = render(<DependencyGraphView {...props} />);

  rerender(<DependencyGraphView {...props} />);
  expect(scheduler).not.toHaveBeenCalled();
  expect(engine.layout).not.toHaveBeenCalled();

  fireEvent.click(screen.getByRole('button', { name: 'Relayout' }));
  await waitFor(() => expect(scheduler).toHaveBeenCalledOnce());
  expect(engine.layout).toHaveBeenCalledOnce();

  rerender(<DependencyGraphView {...viewProps({ focusedItemId: null })} />);
  expect(screen.queryByRole('heading', { name: /Details for/ })).not.toBeInTheDocument();
});
