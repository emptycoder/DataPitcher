import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { getPlanInclusionPathUrl, getPlanReviewUrl, getStartPlanJobUrl } from '../../api/generated/client';
import { fetchInclusionPath, fetchPlanReview, startPlanJob } from './planReviewApi';
import { inclusionPathWire, planId, reviewWire } from '../../test/planFixtures';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
it('sends the bearer header, validates review data, and keeps tokens out of URLs', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify(reviewWire), { status: 200 }));
  await expect(fetchPlanReview(planId, request, authentication, new AbortController().signal)).resolves.toMatchObject({ planId, version: 4 });
  expect(request).toHaveBeenCalledWith(getPlanReviewUrl(planId), expect.objectContaining({ headers: { Authorization: 'Bearer memory-token' } }));
  expect(getPlanReviewUrl(planId)).not.toContain('memory-token');
});
it('posts the inclusion key in the body and validates the path', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify(inclusionPathWire), { status: 200 }));
  await expect(fetchInclusionPath(planId, { table: 'sales.Orders', stableKey: 'Id=42' }, request, authentication, new AbortController().signal)).resolves.toEqual(inclusionPathWire);
  expect(request).toHaveBeenCalledWith(getPlanInclusionPathUrl(planId), expect.objectContaining({ method: 'POST', body: JSON.stringify({ table: 'sales.Orders', stableKey: 'Id=42' }) }));
});
it('starts with an in-memory token and rejects an absent token', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify({ operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', jobId: '22222222-2222-4222-8222-222222222222' }), { status: 202 }));
  await expect(startPlanJob(planId, 'request-1', request, authentication, new AbortController().signal)).resolves.toMatchObject({ state: 'queued' });
  expect(request).toHaveBeenCalledWith(getStartPlanJobUrl(planId), expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer memory-token', 'Idempotency-Key': 'request-1' }) }));
  await authentication.signOut();
  await expect(fetchPlanReview(planId, request, authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
});
