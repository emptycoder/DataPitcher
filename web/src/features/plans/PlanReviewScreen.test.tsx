import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { AppProviders } from '../../app/AppProviders';
import { planId, reviewWire } from '../../test/planFixtures';
import { PlanReviewScreen } from './PlanReviewScreen';

afterEach(cleanup);

it('shows target-satisfied rows and explains that differing non-key values are not refreshed', async () => {
  const review = {
    ...reviewWire,
    tables: [...reviewWire.tables, { ...reviewWire.tables[0]!, state: 'TargetSatisfied', plannedWrites: 0, inserts: 0, updates: 0 }],
  };
  const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(review), { status: 200 }));

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);

  expect(await screen.findByText('Target satisfied')).toBeVisible();
  expect(screen.getByText(/will not move.*non-key values.*will not refresh/i)).toBeVisible();
});

it('starts an eligible plan with an idempotency key and reports its job identifier', async () => {
  const onJobStarted = vi.fn();
  const request = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(String(input).endsWith('/jobs')
    ? { operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', jobId: '22222222-2222-4222-8222-222222222222' }
    : reviewWire), { status: String(input).endsWith('/jobs') ? 202 : 200 }));
  vi.stubGlobal('crypto', { randomUUID: () => 'idempotency-key' });

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={onJobStarted} /></AppProviders>);
  fireEvent.click(await screen.findByRole('button', { name: 'Start transfer' }));

  await vi.waitFor(() => expect(onJobStarted).toHaveBeenCalledWith('22222222-2222-4222-8222-222222222222'));
  expect(request).toHaveBeenLastCalledWith(`/api/plans/${planId}/jobs`, expect.objectContaining({ headers: expect.objectContaining({ 'Idempotency-Key': 'idempotency-key' }) }));
});
