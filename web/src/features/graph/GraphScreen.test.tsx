import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { PermissionsProvider } from '../../auth/permissions';
import { GraphScreen, type GraphScreenProps } from './GraphScreen';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');
const connectionId = '11111111-1111-4111-8111-111111111111';
const snapshotId = '22222222-2222-4222-8222-222222222222';
const planId = '33333333-3333-4333-8333-333333333333';

const planGraph = {
  revision: 'schema-r1',
  plannedTableIds: [],
  tables: [
    { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'unselected' },
    { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'unselected' },
    { id: 'audit', schema: 'sales', name: 'audit', componentId: 'audit', state: 'unselected' },
  ],
  relationships: [{ id: 'FK_orders_customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' }],
};

const snapshot = {
  connectionId,
  snapshotId,
  hash: 'schema-r1',
  capturedAtUtc: '2026-09-03T00:00:00Z',
  tables: [{ schema: 'sales', name: 'orders', columns: [], primaryKey: null }, { schema: 'sales', name: 'customers', columns: [], primaryKey: null }],
  foreignKeys: [{ name: 'FK_orders_customers', childTable: { schema: 'sales', name: 'orders' }, parentTable: { schema: 'sales', name: 'customers' }, childColumns: ['customer_id'], parentColumns: ['id'], isEnforced: true, isTrusted: true }],
};

function renderGraph(props: Omit<GraphScreenProps, 'authentication'>) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><GraphScreen {...props} authentication={authentication} /></QueryClientProvider>);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('GraphScreen', () => {
  it('asks for a graph entry point without making a request', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);

    renderGraph({});

    expect(screen.getByRole('status')).toHaveTextContent('Choose a transfer plan or schema snapshot to view its dependencies.');
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    ['plan', { planId }],
    ['snapshot', { connectionId, snapshotId }],
  ] as const)('shows loading while the %s graph is requested', (entry, props) => {
    void entry;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));

    renderGraph(props);

    expect(screen.getByRole('status')).toHaveTextContent('Loading schema dependency graph.');
  });

  it('renders a plan graph and lists relationships for the selected table', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(planGraph), { status: 200 })));

    renderGraph({ planId });

    await screen.findByRole('group', { name: 'Schema dependency graph' });
    fireEvent.click(screen.getByTestId('table-sales.orders'));
    expect(screen.getByRole('heading', { name: 'Immediate relationships' })).toBeVisible();
    expect(screen.getByRole('cell', { name: 'FK_orders_customers' })).toBeVisible();
  });

  it('shows when a selected plan table has no immediate relationships', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(planGraph), { status: 200 })));

    renderGraph({ planId });

    await screen.findByRole('group', { name: 'Schema dependency graph' });
    fireEvent.click(screen.getByTestId('table-sales.audit'));
    expect(screen.getByText('sales.audit has no immediate relationships.')).toBeVisible();
  });

  it('renders an empty plan graph without inventing tables', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...planGraph, tables: [], relationships: [] }), { status: 200 })));

    renderGraph({ planId });

    expect(await screen.findByText('No tables to display.')).toBeVisible();
  });

  it.each([
    ['plan', { planId }, 'This plan has no sealed source snapshot.'],
    ['snapshot', { connectionId, snapshotId }, 'This schema snapshot was not found.'],
  ] as const)('states the missing %s source when the API returns 404', async (entry, props, message) => {
    void entry;
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })));

    renderGraph(props);

    expect(await screen.findByRole('alert')).toHaveTextContent(message);
  });

  it.each([
    ['plan', { planId }, 'Unable to load the plan schema graph.'],
    ['snapshot', { connectionId, snapshotId }, 'Unable to load the schema snapshot.'],
  ] as const)('states a generic %s graph error', async (entry, props, message) => {
    void entry;
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Unavailable' }), { status: 500 })));

    renderGraph(props);

    expect(await screen.findByRole('alert')).toHaveTextContent(message);
  });

  it('shows the requested schema snapshot graph', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify(snapshot), { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    renderGraph({ connectionId, snapshotId });

    await screen.findByRole('group', { name: 'Schema dependency graph' });
    expect(fetch).toHaveBeenCalledWith(`/api/connections/${connectionId}/snapshots/${snapshotId}`, expect.anything());
  });

  it('uses the newest snapshot when no snapshot id is supplied', async () => {
    const fetch = vi.fn(async (url: string) => new Response(JSON.stringify(url.endsWith('/snapshots') ? [{ snapshotId, hash: 'schema-r1', capturedAtUtc: '2026-09-03T00:00:00Z' }] : snapshot), { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    renderGraph({ connectionId });

    await screen.findByRole('group', { name: 'Schema dependency graph' });
    expect(fetch).toHaveBeenNthCalledWith(1, `/api/connections/${connectionId}/snapshots`, expect.anything());
    expect(fetch).toHaveBeenNthCalledWith(2, `/api/connections/${connectionId}/snapshots/${snapshotId}`, expect.anything());
  });

  it('points the user to a schema scan when a connection has no snapshots', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200 })));

    renderGraph({ connectionId });

    expect(await screen.findByText(/No schema snapshots exist for this connection/)).toHaveTextContent('Run a schema scan');
  });

  it('does not show graph functionality when verified permissions deny it', async () => {
    const fetch = vi.fn(async (url: string) => new Response(JSON.stringify(url === '/api/auth/effective-permissions' ? { principalId: 'operator-1', tenantId: 'tenant-1', permissions: [] } : planGraph), { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><PermissionsProvider authentication={authentication}><GraphScreen planId={planId} authentication={authentication} /></PermissionsProvider></QueryClientProvider>);

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('You do not have permission to view this schema graph.'));
  });
});
