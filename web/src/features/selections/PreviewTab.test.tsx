import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useRef } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { getCountSelectionUrl, getPreviewSelectionUrl, type SelectionRequest } from '../../api/generated/client';
import { PreviewTab, PreviewStatePanel, displayPreviewValue } from './PreviewTab';
import { SelectionWorkbench } from './SelectionWorkbench';
import { productionVirtualizerAdapter, type VirtualizerAdapter } from './virtualGrid';
import { createWorkbenchPreferences } from './workbenchPreferences';

const virtualizer = vi.hoisted(() => ({
  useVirtualizer: vi.fn((options: { count: number; getScrollElement: () => Element | null; estimateSize: () => number }) => {
    void options;
    return {
      getTotalSize: () => 66,
      getVirtualItems: () => [{ index: 1, start: 33 }],
    };
  }),
}));

vi.mock('@tanstack/react-virtual', () => virtualizer);

const visual = { root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id'] }, joins: [], predicate: null } as const;
const selection: SelectionRequest = { mode: 'visual', visual, rawSql: null, parameters: [], schemaRevision: 'schema-1' };
const draft = { connectionId: 'connection-1', selectionId: null, sqlSnapshot: 'snapshot-1', visual, schemaRevision: 'schema-1' };
const visibleRows: VirtualizerAdapter = { useGrid: () => ({ totalHeight: 3200, items: [{ index: 20, start: 640 }, { index: 21, start: 672 }] }) };

afterEach(() => cleanup());

it('virtualizes preview rows, counts only on request, and cancels the changed preview signal', async () => {
  let previewRequests = 0;
  let countRequests = 0;
  let previewStarted = () => {};
  const previewStartedPromise = new Promise<void>((resolve) => { previewStarted = resolve; });
  let previewAborted = false;
  const request = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input) === getCountSelectionUrl()) {
      countRequests += 1;
      return Promise.resolve(new Response(JSON.stringify({ distinctStableKeyCount: 12 })));
    }
    if (String(input) !== getPreviewSelectionUrl()) throw new Error(`Unexpected URL: ${String(input)}`);
    previewRequests += 1;
    if (previewRequests === 1) return Promise.resolve(new Response(JSON.stringify({ columns: ['id'], rows: Array.from({ length: 100 }, (_, id) => ({ id })), hasMore: false, revision: 'schema-1' })));
    return new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => {
        previewAborted = init.signal?.aborted ?? false;
        reject(new DOMException('Aborted', 'AbortError'));
      });
      previewStarted();
    });
  });
  const client = new QueryClient();
  const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token-1');
  const view = render(
    <QueryClientProvider client={client}>
      <PreviewTab virtualizer={visibleRows} queryClient={client} draft={draft} request={request} authentication={authentication} selection={selection} />
    </QueryClientProvider>,
  );

  expect(await screen.findByRole('cell', { name: '20' })).toBeVisible();
  expect(screen.getByRole('cell', { name: '21' })).toBeVisible();
  expect(screen.queryByRole('cell', { name: '0' })).toBeNull();
  expect(countRequests).toBe(0);

  const changedDraft = { ...draft, visual: { ...visual, predicate: { kind: 'null' as const, column: { alias: 'o', name: 'id', valueKind: 'int' as const }, negated: false } } };
  view.rerender(
    <QueryClientProvider client={client}>
      <PreviewTab virtualizer={visibleRows} queryClient={client} draft={changedDraft} request={request} authentication={authentication} selection={selection} />
    </QueryClientProvider>,
  );
  await previewStartedPromise;
  expect(countRequests).toBe(0);

  fireEvent.click(screen.getByRole('button', { name: 'Count distinct stable keys' }));
  expect(await screen.findByText('Distinct stable keys: 12')).toBeVisible();
  expect(screen.getByText('Joined rows are not counted separately.')).toBeVisible();
  expect(countRequests).toBe(1);

  fireEvent.click(screen.getByRole('button', { name: 'Cancel preview' }));
  await waitFor(() => expect(previewAborted).toBe(true));
});

it('renders every preview request state and safe cell display', () => {
  const retry = vi.fn();
  const refresh = vi.fn();
  const expected = [
    ['loading', 'Loading preview'],
    ['empty', 'No rows match this selection'],
    ['error', 'Retry preview'],
    ['forbidden', 'You do not have access to preview this selection.'],
    ['cancelled', 'Preview cancelled'],
    ['stale', 'Refresh preview'],
    ['tokenExpired', 'Sign in to preview this selection.'],
  ] as const;

  for (const [state, text] of expected) {
    render(<PreviewStatePanel state={state} onRetry={retry} onRefresh={refresh} />);
    expect(screen.getByText(text)).toBeVisible();
    cleanup();
  }
  const { container } = render(<PreviewStatePanel state="ready" onRetry={retry} onRefresh={refresh} />);
  expect(container).toBeEmptyDOMElement();
  render(<PreviewStatePanel state="error" onRetry={retry} onRefresh={refresh} />);
  fireEvent.click(screen.getByRole('button', { name: 'Retry preview' }));
  render(<PreviewStatePanel state="stale" onRetry={retry} onRefresh={refresh} />);
  fireEvent.click(screen.getByRole('button', { name: 'Refresh preview' }));
  expect(retry).toHaveBeenCalledOnce();
  expect(refresh).toHaveBeenCalledOnce();
  expect(displayPreviewValue(null)).toBe('NULL');
  expect(displayPreviewValue({ id: 1 })).toBe('[object Object]');
});

it('wraps TanStack Virtual behind the production adapter', () => {
  function Probe() {
    const ref = useRef<HTMLDivElement>(null);
    const grid = productionVirtualizerAdapter.useGrid(2, ref);
    return <output>{`${grid.totalHeight}|${grid.items[0]!.index}|${grid.items[0]!.start}`}</output>;
  }

  render(<Probe />);

  expect(screen.getByRole('status')).toHaveTextContent('66|1|33');
  expect(virtualizer.useVirtualizer).toHaveBeenCalledWith(expect.objectContaining({ count: 2, estimateSize: expect.any(Function), getScrollElement: expect.any(Function) }));
  const options = virtualizer.useVirtualizer.mock.calls[0]![0];
  expect(options.getScrollElement()).toBeNull();
  expect(options.estimateSize()).toBe(32);
});

it('mounts the preview tab in the workbench editor region', () => {
  const root = { tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id'], selected: true };
  const preferences = createWorkbenchPreferences({ getItem: () => null, setItem: () => {}, removeItem: () => {} });

  render(
    <SelectionWorkbench
      tables={[root]}
      root={root}
      selectionName="Orders"
      activeTab="preview"
      preferences={preferences}
      onSelectRoot={vi.fn()}
      onShowColumns={vi.fn()}
      onSelectionNameChange={vi.fn()}
      onTabChange={vi.fn()}
      rightRail={null}
      selection={visual}
      schema={{ tables: [{ tableId: 'sales.orders', stableKey: ['id'], columns: [{ name: 'id', valueKind: 'int' }] }], foreignKeys: [] }}
      onVisualChange={vi.fn()}
      onRequestSqlSnapshot={vi.fn()}
      previewTab={<p>Preview workbench</p>}
    />,
  );

  expect(screen.getByText('Preview workbench')).toBeVisible();
});
