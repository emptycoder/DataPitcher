import { afterEach, expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { getOperationStatus, getPlanReview, requestErrorMessage, savePlan, sealPlan, startPlan } from './sealApi';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const planId = '11111111-1111-4111-8111-111111111111';

afterEach(() => vi.unstubAllGlobals());

it('calls the plan save, review, seal, operation, and start endpoints', async () => {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => { void input; void init; return new Response(JSON.stringify({ operationId: '22222222-2222-4222-8222-222222222222', planId, jobId: '33333333-3333-4333-8333-333333333333' }), { status: 202 }); });
  vi.stubGlobal('fetch', fetch);

  await savePlan(planId, { displayName: 'Orders', operatorNote: 'Production copy', ifMatch: '"1"', selectionId: '44444444-4444-4444-8444-444444444444', sourceConnectionId: '55555555-5555-4555-8555-555555555555', targetConnectionId: '66666666-6666-4666-8666-666666666666' }, authentication);
  await getPlanReview(planId, authentication);
  await sealPlan(planId, authentication);
  await getOperationStatus('22222222-2222-4222-8222-222222222222', authentication);
  await startPlan(planId, 'start-1', authentication);

  expect(fetch).toHaveBeenNthCalledWith(1, `/api/plans/${planId}`, expect.objectContaining({ method: 'PUT', body: JSON.stringify({ displayName: 'Orders', operatorNote: 'Production copy', ifMatch: '"1"', selectionId: '44444444-4444-4444-8444-444444444444', sourceConnectionId: '55555555-5555-4555-8555-555555555555', targetConnectionId: '66666666-6666-4666-8666-666666666666' }) }));
  expect(fetch).toHaveBeenNthCalledWith(2, `/api/plans/${planId}/review`, expect.anything());
  expect(fetch).toHaveBeenNthCalledWith(3, `/api/plans/${planId}/seal`, expect.objectContaining({ method: 'POST' }));
  expect(fetch).toHaveBeenNthCalledWith(4, '/api/operations/22222222-2222-4222-8222-222222222222', expect.anything());
  expect(fetch).toHaveBeenNthCalledWith(5, `/api/plans/${planId}/jobs`, expect.objectContaining({ method: 'POST' }));
  expect(new Headers(fetch.mock.calls[4]![1]!.headers).get('Idempotency-Key')).toBe('start-1');
});

it.each([
  [401, 'Sign in to continue.'],
  [403, 'You do not have permission to do that.'],
  [404, 'The plan was not found.'],
  [409, 'The plan must be sealed before starting a transfer.'],
  [500, 'The service is unavailable. Try again.'],
  [400, 'Fallback.'],
])('maps HTTP %s to a user-facing error', async (status, message) => {
  const { HttpError } = await import('../../api/http');
  expect(requestErrorMessage(new HttpError(status, null), 'Fallback.', 'The plan must be sealed before starting a transfer.')).toBe(message);
});

it('keeps a non-HTTP error as the fallback message', () => {
  expect(requestErrorMessage(new Error('offline'), 'Fallback.', 'Conflict.')).toBe('Fallback.');
});
