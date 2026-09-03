import { afterEach, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { createConnection, fetchConnections, queueConnectionCheck, queueSchemaScan } from './connectionsApi';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const connection = { connectionId: '11111111-1111-4111-8111-111111111111', displayName: 'Warehouse', providerId: 'postgresql', health: 'Healthy', eTag: 'etag-1' };

afterEach(() => vi.unstubAllGlobals());

it('sends the bearer header, validates the list, and keeps the token out of the URL', async () => {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return new Response(JSON.stringify([connection]), { status: 200 }); });
  await expect(fetchConnections(request, authentication, new AbortController().signal)).resolves.toEqual([connection]);
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ headers: { Authorization: 'Bearer memory-token' } }));
  expect(request.mock.calls[0]![0]).not.toContain('memory-token');
});

it('creates a connection with the bearer header and posted body', async () => {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return new Response(JSON.stringify(connection), { status: 200 }); });
  const body = { displayName: 'Warehouse', providerId: 'postgresql', credentialId: '22222222-2222-4222-8222-222222222222', ifMatch: '*' };
  await expect(createConnection(body, request, authentication, new AbortController().signal)).resolves.toEqual(connection);
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ method: 'POST', body: JSON.stringify(body), headers: expect.objectContaining({ Authorization: 'Bearer memory-token', 'Content-Type': 'application/json' }) }));
});

it('queues connection checks and schema scans, then rejects an absent token', async () => {
  const receipt = { operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', statusUri: '/api/operations/33333333-3333-4333-8333-333333333333' };
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return new Response(JSON.stringify(receipt), { status: 202 }); });
  await expect(queueConnectionCheck(connection.connectionId, request, authentication, new AbortController().signal)).resolves.toEqual(receipt);
  expect(request).toHaveBeenCalledWith(`/api/connections/${connection.connectionId}/checks`, expect.objectContaining({ method: 'POST' }));
  await expect(queueSchemaScan(connection.connectionId, request, authentication, new AbortController().signal)).resolves.toEqual(receipt);
  expect(request).toHaveBeenCalledWith(`/api/connections/${connection.connectionId}/schema-scans`, expect.objectContaining({ method: 'POST' }));
  await authentication.signOut();
  await expect(fetchConnections(request, authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
});

it('connections API uses the shared HTTP request when given browser fetch', async () => {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return new Response(JSON.stringify([connection]), { status: 200 }); });
  vi.stubGlobal('fetch', request);
  const browserAuthentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'browser-token');

  await expect(fetchConnections(fetch, browserAuthentication, new AbortController().signal)).resolves.toEqual([connection]);
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ headers: expect.any(Headers) }));
  expect(new Headers(request.mock.calls[0]![1]?.headers).get('Authorization')).toBe('Bearer browser-token');
});
