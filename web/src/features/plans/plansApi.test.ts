import { afterEach, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { getInclusionPath, getPlanReview } from './plansApi';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const planId = '11111111-1111-4111-8111-111111111111';

afterEach(() => vi.unstubAllGlobals());

it('gets the plan review from its plan URL', async () => {
  const review = { planId, seal: { status: 'sealed', invalidationReasons: [] } };
  const fetch = vi.fn(async () => new Response(JSON.stringify(review), { status: 200 }));
  vi.stubGlobal('fetch', fetch);

  await expect(getPlanReview(planId, authentication, new AbortController().signal)).resolves.toEqual(review);
  expect(fetch).toHaveBeenCalledWith(`/api/plans/${planId}/review`, expect.objectContaining({ signal: expect.any(AbortSignal) }));
});

it('posts a table stable key to get its inclusion path', async () => {
  const path = { table: 'sales.Orders', stableKey: 'Id=42', rootSelection: 'Open orders', steps: [] };
  const fetch = vi.fn(async () => new Response(JSON.stringify(path), { status: 200 }));
  vi.stubGlobal('fetch', fetch);

  await expect(getInclusionPath(planId, 'sales.Orders', 'Id=42', authentication, new AbortController().signal)).resolves.toEqual(path);
  expect(fetch).toHaveBeenCalledWith(`/api/plans/${planId}/inclusion-paths`, expect.objectContaining({ method: 'POST', body: JSON.stringify({ table: 'sales.Orders', stableKey: 'Id=42' }) }));
});
