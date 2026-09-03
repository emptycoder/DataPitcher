import { afterEach, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { HttpError, requestJson } from './http';

const principal = { subjectId: 'operator-1', tenantId: 'tenant-1' };

afterEach(() => {
  vi.unstubAllGlobals();
});

it('sends JSON with the caller bearer token and parses the response', async () => {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    expect(input).toBe('/api/connections');
    expect(init?.method).toBe('POST');
    return new Response(JSON.stringify({ id: 'connection-1' }), { status: 201 });
  });
  vi.stubGlobal('fetch', request);
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');
  const signal = new AbortController().signal;

  await expect(requestJson<{ id: string }>('/api/connections', authentication, { method: 'POST', body: { displayName: 'Warehouse' }, signal }))
    .resolves.toEqual({ id: 'connection-1' });

  const init = request.mock.calls[0]?.[1];
  expect(request).toHaveBeenCalledWith('/api/connections', expect.objectContaining({ method: 'POST', body: JSON.stringify({ displayName: 'Warehouse' }), signal }));
  expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer memory-token');
  expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');
});

it.each([401, 403, 404, 500])('exposes status %i and the server problem on failed responses', async (status) => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ detail: `status ${status}` }), { status })));
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');

  await requestJson('/api/resource', authentication).then(
    () => { throw new Error('Expected an HTTP error.'); },
    (error: unknown) => {
      expect(error).toBeInstanceOf(HttpError);
      expect(error).toMatchObject({ status, problem: { detail: `status ${status}` } });
    },
  );
});

it('keeps an HTTP failure typed when its body is not JSON', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response('service unavailable', { status: 503 })));
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');

  await expect(requestJson('/api/resource', authentication)).rejects.toMatchObject({ status: 503, problem: null });
});

it('represents an absent access token as an unauthorized HTTP error', async () => {
  const request = vi.fn();
  vi.stubGlobal('fetch', request);
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');
  await authentication.signOut();

  await expect(requestJson('/api/resource', authentication)).rejects.toMatchObject({ status: 401, problem: { detail: 'Not authenticated.' } });
  expect(request).not.toHaveBeenCalled();
});
