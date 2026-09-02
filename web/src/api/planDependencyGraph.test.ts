import { QueryClient } from '@tanstack/react-query';
import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { fetchPlanDependencyGraph } from './planDependencyGraphApi';
import { planDependencyGraphQueryOptions } from './planDependencyGraphQuery';

const graph = { revision: 'schema-r7', plannedTableIds: ['orders'], tables: [
  { id: 'orders', schema: 'sales', name: 'orders', componentId: 'scc:sales.orders', state: 'root-selected' },
  { id: 'customers', schema: 'sales', name: 'customers', componentId: 'scc:sales.customers', state: 'required-dependency' },
], relationships: [{ id: 'orders-customer', name: 'FK_orders_customer', childTableId: 'orders', parentTableId: 'customers' }] };
const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');

it('validates topology before it can enter the Query cache', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify(graph), { status: 200 }));
  const queryOptions = planDependencyGraphQueryOptions('plan-1', request, authentication);
  await expect(fetchPlanDependencyGraph('plan-1', request, authentication, new AbortController().signal)).resolves.toEqual(graph);
  await expect(new QueryClient().fetchQuery(queryOptions)).resolves.toEqual(graph);
  expect(queryOptions.queryKey).toEqual(['planDependencyGraph', 'plan-1']);
  expect(request).toHaveBeenCalledWith('/api/plans/plan-1/schema-dependency-graph', expect.objectContaining({ headers: { Authorization: 'Bearer token' } }));
});

it('rejects malformed topology instead of caching it', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify({ revision: 'r1' }), { status: 200 }));
  await expect(fetchPlanDependencyGraph('plan-1', request, authentication, new AbortController().signal)).rejects.toThrow();
});

it('rejects an absent access token before making a graph request', async () => {
  const signedOutAuthentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');
  await signedOutAuthentication.signOut();
  await expect(fetchPlanDependencyGraph('plan-1', vi.fn(), signedOutAuthentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
});
