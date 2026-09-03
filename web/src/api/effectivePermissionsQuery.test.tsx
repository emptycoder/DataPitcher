import { QueryClient, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { AppProviders } from '../app/AppProviders';
import { getEffectivePermissionsUrl } from './generated/client';
import { fetchEffectivePermissions } from './effectivePermissionsApi';
import { effectivePermissionsQueryOptions } from './effectivePermissionsQuery';

const principal = { subjectId: 'operator-1', tenantId: 'tenant-1' };
function QueryClientProbe({ onClient }: Readonly<{ onClient: (client: QueryClient) => void }>) {
  const client = useQueryClient();
  useEffect(() => { onClient(client); }, [client, onClient]);
  return <output>query-ready</output>;
}

it('validates injected-fetch data before Query resolves it', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] }), { status: 200 }));
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
  const client = new QueryClient();
  await expect(client.fetchQuery(effectivePermissionsQueryOptions(principal, request, authentication)))
    .resolves.toEqual({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] });
  expect(request).toHaveBeenCalledWith(getEffectivePermissionsUrl(), expect.objectContaining({ headers: { Authorization: 'Bearer development-token' } }));
});

it('rejects malformed data instead of putting it in Query', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify({ permissions: [] }), { status: 200 }));
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
  await expect(new QueryClient().fetchQuery(effectivePermissionsQueryOptions(principal, request, authentication))).rejects.toThrow();
});

it('rejects an absent token and retains an injected or default Query client', async () => {
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
  await authentication.signOut();
  await expect(fetchEffectivePermissions(vi.fn(), authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
  const injected = new QueryClient();
  const observedClients: QueryClient[] = [];
  const { unmount } = render(<AppProviders client={injected}><QueryClientProbe onClient={(client) => observedClients.push(client)} /></AppProviders>);
  expect(screen.getByRole('status')).toHaveTextContent('query-ready');
  expect(observedClients.at(-1)).toBe(injected);
  unmount();
  render(<AppProviders><QueryClientProbe onClient={(client) => observedClients.push(client)} /></AppProviders>);
  expect(observedClients.at(-1)).toBeInstanceOf(QueryClient);
});
