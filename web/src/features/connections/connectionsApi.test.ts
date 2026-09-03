import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { createConnection, fetchConnections, queueConnectionCheck } from './connectionsApi';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const connection = { connectionId: '11111111-1111-4111-8111-111111111111', displayName: 'Warehouse', providerId: 'postgresql', health: 'Healthy', eTag: 'etag-1' };

it('sends the bearer header, validates the list, and keeps the token out of the URL', async () => {
  const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify([connection]), { status: 200 }));
  await expect(fetchConnections(request, authentication, new AbortController().signal)).resolves.toEqual([connection]);
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ headers: { Authorization: 'Bearer memory-token' } }));
  expect(request.mock.calls[0]![0]).not.toContain('memory-token');
});

it('creates a connection with the bearer header and posted body', async () => {
  const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(connection), { status: 200 }));
  const body = { displayName: 'Warehouse', providerId: 'postgresql', credentialId: '22222222-2222-4222-8222-222222222222', ifMatch: '*' };
  await expect(createConnection(body, request, authentication, new AbortController().signal)).resolves.toEqual(connection);
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ method: 'POST', body: JSON.stringify(body), headers: expect.objectContaining({ Authorization: 'Bearer memory-token', 'Content-Type': 'application/json' }) }));
});

it('queues a connection health check and rejects an absent token', async () => {
  const receipt = { operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', statusUri: '/api/operations/33333333-3333-4333-8333-333333333333' };
  const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(receipt), { status: 202 }));
  await expect(queueConnectionCheck(connection.connectionId, request, authentication, new AbortController().signal)).resolves.toEqual(receipt);
  expect(request).toHaveBeenCalledWith(`/api/connections/${connection.connectionId}/checks`, expect.objectContaining({ method: 'POST' }));
  await authentication.signOut();
  await expect(fetchConnections(request, authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
});
