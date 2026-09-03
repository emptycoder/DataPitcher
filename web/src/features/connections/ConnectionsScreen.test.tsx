import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { sessionActions } from '../../stores/sessionStore';
import { ConnectionsScreen } from './ConnectionsScreen';

const connection = { connectionId: '11111111-1111-4111-8111-111111111111', displayName: 'Warehouse', providerId: 'sqlserver', health: 'Unknown', eTag: 'etag-1' };
const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderConnections(request: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  return render(<AppProviders client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><ConnectionsScreen request={request} authentication={authentication} /></AppProviders>);
}

afterEach(() => {
  cleanup();
  sessionActions.setConnectionIds(null, null);
});

it('connections management shows an empty connection list', async () => {
  renderConnections(vi.fn(async () => response([])));

  expect(await screen.findByText('No connections registered.')).toBeVisible();
});

it.each([
  [401, 'Sign in to manage connections.'],
  [403, 'You do not have permission to manage connections.'],
  [404, 'The connection was not found. Refresh and try again.'],
  [500, 'Connection service is unavailable. Try again.'],
])('connections management identifies a %i connection response', async (status, message) => {
  renderConnections(vi.fn(async () => response({ detail: 'offline' }, status)));

  expect(await screen.findByRole('alert')).toHaveTextContent(message);
});

it('connections management uses a generic message for an unexpected load response', async () => {
  renderConnections(vi.fn(async () => response({ detail: 'conflict' }, 409)));

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load connections.');
});

it('connections management uses a generic message when loading fails before a response', async () => {
  renderConnections(vi.fn(async () => { throw new Error('offline'); }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load connections.');
});

it('connections management creates a connection and clears its credential reference', async () => {
  let created = false;
  const request = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') {
      created = true;
      return response(connection);
    }
    return response(created ? [connection] : []);
  });
  renderConnections(request);

  await screen.findByText('No connections registered.');
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Warehouse' } });
  fireEvent.change(screen.getByLabelText('Credential ID'), { target: { value: '22222222-2222-4222-8222-222222222222' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add connection' }));

  expect(await screen.findByRole('cell', { name: 'Warehouse' })).toBeVisible();
  expect(screen.getByLabelText('Credential ID')).toHaveAttribute('type', 'password');
  expect(screen.getByLabelText('Credential ID')).toHaveValue('');
});

it('connections management identifies the missing credential reference before creating', async () => {
  const request = vi.fn(async () => response([]));
  renderConnections(request);

  await screen.findByText('No connections registered.');
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Warehouse' } });
  fireEvent.submit(screen.getByRole('form', { name: 'Add connection' }));

  expect(screen.getByRole('alert')).toHaveTextContent('Credential ID is required.');
  expect(request).toHaveBeenCalledOnce();
});

it('connections management identifies the missing display name before creating', async () => {
  const request = vi.fn(async () => response([]));
  renderConnections(request);

  await screen.findByText('No connections registered.');
  fireEvent.change(screen.getByLabelText('Credential ID'), { target: { value: '22222222-2222-4222-8222-222222222222' } });
  fireEvent.submit(screen.getByRole('form', { name: 'Add connection' }));

  expect(screen.getByRole('alert')).toHaveTextContent('Display name is required.');
  expect(request).toHaveBeenCalledOnce();
});

it('connections management puts a server validation error on the credential field', async () => {
  const request = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST'
    ? response({ errors: { CredentialId: ['Credential ID is invalid.'] } }, 400)
    : response([]));
  renderConnections(request);

  await screen.findByText('No connections registered.');
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Warehouse' } });
  fireEvent.change(screen.getByLabelText('Credential ID'), { target: { value: 'not-a-guid' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add connection' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Credential ID is invalid.');
});

it('connections management reports a failed connection creation without field errors', async () => {
  const request = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST' ? response({ detail: 'unavailable' }, 500) : response([]));
  renderConnections(request);

  await screen.findByText('No connections registered.');
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Warehouse' } });
  fireEvent.change(screen.getByLabelText('Credential ID'), { target: { value: '22222222-2222-4222-8222-222222222222' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add connection' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Connection service is unavailable. Try again.');
});

it('connections management presents a successful health check result', async () => {
  let health = 'Unknown';
  const request = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).endsWith('/checks')) {
      health = 'Healthy';
      return response({ operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', statusUri: '/api/operations/33333333-3333-4333-833333333333', connectionId: connection.connectionId });
    }
    return response([{ ...connection, health }]);
  });
  renderConnections(request);

  await screen.findByText('Unknown');
  fireEvent.click(screen.getByRole('button', { name: 'Use as source' }));
  fireEvent.click(screen.getByRole('button', { name: 'Use as target' }));
  expect(screen.getByRole('button', { name: 'Use as source' })).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByRole('button', { name: 'Use as target' })).toHaveAttribute('aria-pressed', 'true');
  fireEvent.click(screen.getByRole('button', { name: 'Check health' }));

  expect(await screen.findByText('Health check result: Healthy.')).toBeVisible();
});

it('connections management presents an unhealthy check result as information', async () => {
  let health = 'Unknown';
  const request = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).endsWith('/checks')) {
      health = 'Unhealthy';
      return response({ operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', statusUri: '/api/operations/33333333-3333-4333-833333333333', connectionId: connection.connectionId });
    }
    return response([{ ...connection, health }]);
  });
  renderConnections(request);

  await screen.findByText('Unknown');
  fireEvent.click(screen.getByRole('button', { name: 'Check health' }));

  expect(await screen.findByText('Health check result: Unhealthy.')).toBeVisible();
  expect(screen.getByText('Unhealthy')).toHaveAttribute('data-tone', 'danger');
});

it('connections management reports a failed health check', async () => {
  const request = vi.fn(async (input: RequestInfo | URL) => String(input).endsWith('/checks') ? response({ detail: 'unavailable' }, 500) : response([connection]));
  renderConnections(request);

  fireEvent.click(await screen.findByRole('button', { name: 'Check health' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Connection service is unavailable. Try again.');
});

it('connections management shows a schema scan as pending after it starts', async () => {
  const request = vi.fn(async (input: RequestInfo | URL) => response(
    String(input).endsWith('/schema-scans')
      ? { operationId: '44444444-4444-4444-8444-444444444444', state: 'queued', statusUri: '/api/operations/44444444-4444-4444-8444-444444444444', connectionId: connection.connectionId }
      : [connection],
    String(input).endsWith('/schema-scans') ? 202 : 200,
  ));
  renderConnections(request);

  await screen.findByText('Warehouse');
  fireEvent.click(screen.getByRole('button', { name: 'Scan schema' }));

  expect(await screen.findByText('Schema scan queued. It is running in the background.')).toBeVisible();
  expect(request).toHaveBeenCalledWith(`/api/connections/${connection.connectionId}/schema-scans`, expect.objectContaining({ method: 'POST' }));
});

it('connections management reports a failed schema scan request', async () => {
  const request = vi.fn(async (input: RequestInfo | URL) => String(input).endsWith('/schema-scans') ? response({ detail: 'unavailable' }, 500) : response([connection]));
  renderConnections(request);

  fireEvent.click(await screen.findByRole('button', { name: 'Scan schema' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Connection service is unavailable. Try again.');
});
