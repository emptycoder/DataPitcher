import { afterEach, describe, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { fetchLatestSnapshotGraph, fetchPlanGraph, fetchSnapshotGraph } from './graphApi';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');
const connectionId = '11111111-1111-4111-8111-111111111111';
const snapshotId = '22222222-2222-4222-8222-222222222222';

const snapshot = {
  connectionId,
  snapshotId,
  hash: 'schema-r1',
  capturedAtUtc: '2026-09-03T00:00:00Z',
  tables: [
    { schema: 'sales', name: 'orders', columns: [], primaryKey: null },
    { schema: 'sales', name: 'customers', columns: [], primaryKey: null },
  ],
  foreignKeys: [{ name: 'FK_orders_customers', childTable: { schema: 'sales', name: 'orders' }, parentTable: { schema: 'sales', name: 'customers' }, childColumns: ['customer_id'], parentColumns: ['id'], isEnforced: true, isTrusted: true }],
};

afterEach(() => vi.unstubAllGlobals());

describe('graphApi', () => {
  it('projects a plan graph into table addresses and foreign-key edges', async () => {
    const fetch = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
      void _input;
      void _init;
      return new Response(JSON.stringify({
        revision: 'schema-r1',
        plannedTableIds: [],
        tables: [{ id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'unselected' }, { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'unselected' }],
        relationships: [{ id: 'FK_orders_customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' }],
      }), { status: 200 });
    });
    vi.stubGlobal('fetch', fetch);

    await expect(fetchPlanGraph('33333333-3333-4333-8333-333333333333', authentication, new AbortController().signal)).resolves.toEqual({ tables: [{ schema: 'sales', name: 'orders' }, { schema: 'sales', name: 'customers' }], edges: [{ foreignKeyName: 'FK_orders_customers', child: { schema: 'sales', name: 'orders' }, parent: { schema: 'sales', name: 'customers' } }] });
    expect(fetch).toHaveBeenCalledWith('/api/plans/33333333-3333-4333-8333-333333333333/schema-dependency-graph', expect.objectContaining({ signal: expect.any(AbortSignal) }));
    expect(new Headers(fetch.mock.calls[0]![1]?.headers).get('Authorization')).toBe('Bearer token');
  });

  it('projects a requested schema snapshot into the graph view shape', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify(snapshot), { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    await expect(fetchSnapshotGraph(connectionId, snapshotId, authentication, new AbortController().signal)).resolves.toEqual({ tables: [{ schema: 'sales', name: 'orders' }, { schema: 'sales', name: 'customers' }], edges: [{ foreignKeyName: 'FK_orders_customers', child: { schema: 'sales', name: 'orders' }, parent: { schema: 'sales', name: 'customers' } }] });
    expect(fetch).toHaveBeenCalledWith(`/api/connections/${connectionId}/snapshots/${snapshotId}`, expect.anything());
  });

  it('loads the newest schema snapshot when one is available', async () => {
    const fetch = vi.fn(async (url: string) => new Response(JSON.stringify(url.endsWith('/snapshots') ? [{ snapshotId, hash: 'schema-r1', capturedAtUtc: '2026-09-03T00:00:00Z' }] : snapshot), { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    await expect(fetchLatestSnapshotGraph(connectionId, authentication, new AbortController().signal)).resolves.toEqual({ tables: [{ schema: 'sales', name: 'orders' }, { schema: 'sales', name: 'customers' }], edges: [{ foreignKeyName: 'FK_orders_customers', child: { schema: 'sales', name: 'orders' }, parent: { schema: 'sales', name: 'customers' } }] });
    expect(fetch).toHaveBeenNthCalledWith(1, `/api/connections/${connectionId}/snapshots`, expect.anything());
    expect(fetch).toHaveBeenNthCalledWith(2, `/api/connections/${connectionId}/snapshots/${snapshotId}`, expect.anything());
  });

  it('returns no graph when a connection has no snapshots', async () => {
    const fetch = vi.fn(async () => new Response('[]', { status: 200 }));
    vi.stubGlobal('fetch', fetch);

    await expect(fetchLatestSnapshotGraph(connectionId, authentication, new AbortController().signal)).resolves.toBeNull();
    expect(fetch).toHaveBeenCalledOnce();
  });
});
