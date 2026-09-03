import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { createDevelopmentAuthenticationAdapter } from './authAdapter';
import { PermissionsProvider, usePermissions } from './permissions';

const principal = { subjectId: 'operator-1', tenantId: 'tenant-1' };

function PermissionProbe() {
  const { hasPermission, isVerified } = usePermissions();
  return <output>{`${isVerified}:${hasPermission('Connections.Read')}:${hasPermission('Transfers.Start')}`}</output>;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it('loads effective permissions once and answers verified permission checks', async () => {
  const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    expect(input).toBe('/api/auth/effective-permissions');
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer memory-token');
    return new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Connections.Read'] }), { status: 200 });
  });
  vi.stubGlobal('fetch', request);
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');
  render(<PermissionsProvider authentication={authentication}><PermissionProbe /></PermissionsProvider>);

  expect(screen.getByRole('status')).toHaveTextContent('false:true:true');
  expect(await screen.findByText('true:true:false')).toBeVisible();
  expect(request).toHaveBeenCalledTimes(1);
});

it('keeps permissions unverified and permitted when the route is not registered', async () => {
  const request = vi.fn(async () => new Response('', { status: 404 }));
  vi.stubGlobal('fetch', request);
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');
  render(<PermissionsProvider authentication={authentication}><PermissionProbe /></PermissionsProvider>);

  await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
  expect(screen.getByRole('status')).toHaveTextContent('false:true:true');
});

it('keeps permissions unverified and permitted for malformed or failed responses', async () => {
  const request = vi.fn(async () => new Response('', { status: 500 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ permissions: [1] }), { status: 200 }))
    .mockResolvedValueOnce(new Response('', { status: 500 }));
  vi.stubGlobal('fetch', request);
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'memory-token');
  const { rerender } = render(<PermissionsProvider authentication={authentication}><PermissionProbe /></PermissionsProvider>);

  await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
  expect(screen.getByRole('status')).toHaveTextContent('false:true:true');
  rerender(<PermissionsProvider authentication={createDevelopmentAuthenticationAdapter(principal, 'replacement-token')}><PermissionProbe /></PermissionsProvider>);
  await waitFor(() => expect(request).toHaveBeenCalledTimes(2));
  expect(screen.getByRole('status')).toHaveTextContent('false:true:true');
});

it('permits unknown permissions when no provider is present', () => {
  render(<PermissionProbe />);

  expect(screen.getByRole('status')).toHaveTextContent('false:true:true');
});
