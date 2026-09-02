import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { graphViewActions } from '../stores/graphViewStore';
import type { LayoutEngine, LayoutResult, LayoutScheduler } from './layout';
import { createLayoutResultCache } from './layout';
import type { GraphTopology, VisibleItem, VisibleRelationship } from './model';

type CapturedViewProps = Readonly<{
  items: readonly VisibleItem[];
  relationships: readonly VisibleRelationship[];
  positions: Readonly<Record<string, Readonly<{ x: number; y: number }>>>;
  onSelect: (itemId: string) => void;
  onFocus: (itemId: string) => void;
  onExpandDependencies: (itemId: string) => void;
  onExpandDependants: (itemId: string) => void;
  onViewportChange: (viewport: Readonly<{ x: number; y: number; zoom: number }>) => void;
  onPinnedPositionChange: (itemId: string, position: Readonly<{ x: number; y: number }>) => void;
  onMeasure: (itemId: string, size: Readonly<{ width: number; height: number }>) => void;
  onRelayout: () => void;
}>;

const view = vi.hoisted(() => ({ props: undefined as unknown }));

vi.mock('./DependencyGraphView', async () => {
  const { createElement } = await import('react');
  return {
    DependencyGraphView: (props: CapturedViewProps) => {
      view.props = props;
      return createElement('output', { 'aria-label': 'Visible tables' }, props.items.map((item) => item.memberIds.join(',')).join(','));
    },
  };
});

import { DependencyGraphScreen, type DependencyGraphScreenProps } from './DependencyGraphScreen';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');
const topology = {
  revision: 'r1',
  plannedTableIds: ['orders'],
  tables: [
    { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'root-selected' },
    { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'required-dependency' },
    { id: 'order-lines', schema: 'sales', name: 'order-lines', componentId: 'order-lines', state: 'unselected' },
  ],
  relationships: [
    { id: 'orders-customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' },
    { id: 'lines-orders', name: 'FK_lines_orders', childTableId: 'order-lines', parentTableId: 'orders' },
  ],
} satisfies GraphTopology;

function capturedView() {
  return view.props as CapturedViewProps;
}

function graphAdapter() {
  const layout = vi.fn(async (key: string, graph: Readonly<{ items: readonly VisibleItem[]; relationships: readonly VisibleRelationship[] }>): Promise<LayoutResult> => ({
    key,
    positions: Object.fromEntries(graph.items.map((item, index) => [item.id, { x: index * 200, y: 0 }])),
    edgeSections: Object.fromEntries(graph.relationships.map((relationship) => [relationship.id, []])),
  }));
  return { layout, dispose: vi.fn() };
}

function renderGraph(graph: GraphTopology = topology) {
  const client = new QueryClient();
  const request = vi.fn(async () => new Response(JSON.stringify(graph), { status: 200 }));
  const layoutAdapter = graphAdapter();
  const cache = createLayoutResultCache();
  const scheduler: LayoutScheduler = (work) => work();
  const props: DependencyGraphScreenProps = { planId: 'plan-1', request, authentication, layoutAdapter, cache, scheduler };
  const renderScreen = (next: DependencyGraphScreenProps = props) => (
    <QueryClientProvider client={client}>
      <DependencyGraphScreen {...next} />
    </QueryClientProvider>
  );
  return { cache, client, layoutAdapter, props, renderScreen, ...render(renderScreen()) };
}

afterEach(() => {
  cleanup();
  graphViewActions.setViewport({ x: 0, y: 0, zoom: 1 });
  graphViewActions.setFocusedTableId(null);
  graphViewActions.setGraphTableSelected('orders', false);
  graphViewActions.setGraphTableExpanded('orders', false);
  graphViewActions.clearPinnedGraphPosition('orders');
  vi.clearAllMocks();
});

it('asks the operator to choose a plan without making a request', () => {
  const request = vi.fn();
  const engine: LayoutEngine & Readonly<{ dispose: () => void }> = { layout: async (key): Promise<LayoutResult> => ({ key, positions: {}, edgeSections: {} }), dispose: vi.fn() };
  const scheduler: LayoutScheduler = async (work) => work();

  render(
    <QueryClientProvider client={new QueryClient()}>
      <DependencyGraphScreen
        planId={null}
        request={request}
        authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token')}
        layoutAdapter={engine}
        cache={createLayoutResultCache()}
        scheduler={scheduler}
      />
    </QueryClientProvider>,
  );

  expect(screen.getByRole('status')).toHaveTextContent('Choose a transfer plan to view its dependencies.');
  expect(request).not.toHaveBeenCalled();
});

it('lays out the default child-to-parent plan subgraph only when semantic inputs change', async () => {
  const { cache, client, layoutAdapter, props, renderScreen, rerender, unmount } = renderGraph();

  await waitFor(() => expect(screen.getByLabelText('Visible tables')).toHaveTextContent('customers,orders'));
  act(() => {
    capturedView().onMeasure('orders', { width: 180, height: 60 });
    capturedView().onMeasure('customers', { width: 180, height: 60 });
  });
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledOnce());
  expect(capturedView().positions).toEqual({ customers: { x: 0, y: 0 }, orders: { x: 200, y: 0 } });

  const get = vi.spyOn(cache, 'get');
  rerender(renderScreen({ ...props, scheduler: (work) => work() }));
  await waitFor(() => expect(get).toHaveBeenCalled());
  expect(layoutAdapter.layout).toHaveBeenCalledOnce();

  rerender(renderScreen({ ...props, optionsVersion: 'dependency-graph-v2' }));
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledTimes(2));
  act(() => capturedView().onMeasure('orders', { width: 200, height: 60 }));
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledTimes(3));
  act(() => capturedView().onExpandDependencies('orders'));
  await waitFor(() => expect(screen.getByLabelText('Visible tables')).toHaveTextContent('customers,order-lines,orders'));
  act(() => capturedView().onMeasure('order-lines', { width: 180, height: 60 }));
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledTimes(4));
  act(() => client.setQueryData(['planDependencyGraph', 'plan-1'], { ...topology, revision: 'r2' }));
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledTimes(5));
  act(() => capturedView().onRelayout());
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledTimes(6));

  act(() => {
    capturedView().onFocus('orders');
    capturedView().onSelect('orders');
    capturedView().onSelect('orders');
    capturedView().onViewportChange({ x: 1, y: 2, zoom: 3 });
    capturedView().onPinnedPositionChange('orders', { x: 300, y: 400 });
  });
  await waitFor(() => expect(screen.getByLabelText('Graph viewport')).toHaveTextContent('1,2,3'));
  expect(layoutAdapter.layout).toHaveBeenCalledTimes(6);

  unmount();
  expect(layoutAdapter.dispose).toHaveBeenCalledOnce();
});

it('discards a layout result that arrives after unmount', async () => {
  let resolveLayout!: (result: LayoutResult) => void;
  const layoutAdapter = {
    layout: vi.fn((_key: string) => new Promise<LayoutResult>((resolve) => { resolveLayout = resolve; })),
    dispose: vi.fn(),
  };
  const client = new QueryClient();
  const request = vi.fn(async () => new Response(JSON.stringify(topology), { status: 200 }));
  const cache = createLayoutResultCache();
  const { unmount } = render(
    <QueryClientProvider client={client}>
      <DependencyGraphScreen planId="plan-1" request={request} authentication={authentication} layoutAdapter={layoutAdapter} cache={cache} scheduler={(work) => work()} />
    </QueryClientProvider>,
  );

  await waitFor(() => expect(screen.getByLabelText('Visible tables')).toHaveTextContent('customers,orders'));
  act(() => {
    capturedView().onMeasure('orders', { width: 180, height: 60 });
    capturedView().onMeasure('customers', { width: 180, height: 60 });
  });
  await waitFor(() => expect(layoutAdapter.layout).toHaveBeenCalledOnce());
  const key = layoutAdapter.layout.mock.calls[0]![0];
  unmount();
  resolveLayout({ key, positions: {}, edgeSections: {} });
  await Promise.resolve();
  expect(layoutAdapter.dispose).toHaveBeenCalledOnce();
});

it('reports loading and failed graph requests', async () => {
  const pending = new Promise<Response>(() => {});
  const pendingAdapter = graphAdapter();
  const failedAdapter = graphAdapter();
  const client = new QueryClient();
  const { rerender } = render(
    <QueryClientProvider client={client}>
      <DependencyGraphScreen
        planId="plan-1"
        request={async () => pending}
        authentication={authentication}
        layoutAdapter={pendingAdapter}
        cache={createLayoutResultCache()}
        scheduler={(work) => work()}
      />
    </QueryClientProvider>,
  );
  expect(screen.getByRole('status')).toHaveTextContent('Loading dependency graph.');

  rerender(
    <QueryClientProvider client={new QueryClient()}>
      <DependencyGraphScreen
        planId="plan-2"
        request={async () => { throw new Error('Unavailable'); }}
        authentication={authentication}
        layoutAdapter={failedAdapter}
        cache={createLayoutResultCache()}
        scheduler={(work) => work()}
      />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Unable to load dependency graph.'));
});

it('refuses an expansion that exceeds the visible-table cap', async () => {
  const cappedTopology = {
    revision: 'r1',
    plannedTableIds: Array.from({ length: 200 }, (_, index) => `t${index}`),
    tables: [
      ...Array.from({ length: 200 }, (_, index) => ({ id: `t${index}`, schema: 'sales', name: `t${index}`, componentId: `t${index}`, state: 'root-selected' as const })),
      { id: 'overflow', schema: 'sales', name: 'overflow', componentId: 'overflow', state: 'unselected' as const },
    ],
    relationships: [{ id: 'overflow-t0', name: 'FK_overflow_t0', childTableId: 'overflow', parentTableId: 't0' }],
  } satisfies GraphTopology;
  renderGraph(cappedTopology);

  await waitFor(() => expect(capturedView().items).toHaveLength(200));
  act(() => capturedView().onExpandDependants('t1'));
  act(() => capturedView().onExpandDependants('t0'));
  expect(screen.getByText(/Showing more than 200 tables is disabled/)).toBeVisible();
});
