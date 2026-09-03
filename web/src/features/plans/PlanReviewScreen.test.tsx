import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { AppProviders } from '../../app/AppProviders';
import { planId, reviewWire } from '../../test/planFixtures';
import { PlanReviewScreen } from './PlanReviewScreen';

afterEach(cleanup);

it('asks the operator to choose a plan before loading a review', () => {
  const request = vi.fn();

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={null} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);

  expect(screen.getByRole('status')).toHaveTextContent('Choose a transfer plan to review.');
  expect(request).not.toHaveBeenCalled();
});

it('reports an unavailable plan review', async () => {
  const request = vi.fn(async () => new Response(JSON.stringify({ detail: 'unavailable' }), { status: 500 }));

  render(<AppProviders client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);

  expect(await screen.findByText('Unable to load plan review.')).toBeVisible();
});

it('shows target-satisfied rows and explains that differing non-key values are not refreshed', async () => {
  const review = {
    ...reviewWire,
    tables: [...reviewWire.tables, { ...reviewWire.tables[0]!, state: 'TargetSatisfied', plannedWrites: 0, inserts: 0, updates: 0 }],
  };
  const request = vi.fn(async () => new Response(JSON.stringify(review), { status: 200 }));

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);

  expect(await screen.findByText('Target satisfied')).toBeVisible();
  expect(screen.getByText(/will not move.*non-key values.*will not refresh/i)).toBeVisible();
});

it('starts an eligible plan with an idempotency key and reports its job identifier', async () => {
  const onJobStarted = vi.fn();
  const request = vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).endsWith('/jobs')
    ? { operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', jobId: '22222222-2222-4222-8222-222222222222' }
    : reviewWire), { status: String(input).endsWith('/jobs') ? 202 : 200 }));
  vi.stubGlobal('crypto', { randomUUID: () => 'idempotency-key' });

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={onJobStarted} /></AppProviders>);
  fireEvent.click(await screen.findByRole('button', { name: 'Start transfer' }));

  await vi.waitFor(() => expect(onJobStarted).toHaveBeenCalledWith('22222222-2222-4222-8222-222222222222'));
  expect(request).toHaveBeenLastCalledWith(`/api/plans/${planId}/jobs`, expect.objectContaining({ headers: expect.objectContaining({ 'Idempotency-Key': 'idempotency-key' }) }));
});

it('prevents transfer start and shows server-supplied blockers', async () => {
  const review = {
    ...reviewWire,
    seal: { status: 'invalidated', invalidationReasons: [{ code: 'target-schema', message: 'Target schema changed.' }] },
    startPreconditions: [{ code: 'schemaValid', satisfied: false, message: 'Target schema validation failed.' }],
  };
  const request = vi.fn(async () => new Response(JSON.stringify(review), { status: 200 }));

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);

  expect(await screen.findByRole('alert')).toHaveTextContent('Target schema changed.');
  expect(screen.getByRole('alert')).toHaveTextContent('Target schema validation failed.');
  expect(screen.getByRole('button', { name: 'Start transfer' })).toBeDisabled();
});

it('reports a failed transfer start after the review loads', async () => {
  const request = vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).endsWith('/jobs') ? { detail: 'unavailable' } : reviewWire), { status: String(input).endsWith('/jobs') ? 500 : 200 }));

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);
  fireEvent.click(await screen.findByRole('button', { name: 'Start transfer' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to start transfer.');
});

it('shows that transfer start is in progress', async () => {
  const request = vi.fn((input: RequestInfo | URL) => String(input).endsWith('/jobs') ? new Promise<Response>(() => {}) : Promise.resolve(new Response(JSON.stringify(reviewWire), { status: 200 })));

  render(<AppProviders client={new QueryClient()}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} onJobStarted={vi.fn()} /></AppProviders>);
  fireEvent.click(await screen.findByRole('button', { name: 'Start transfer' }));

  expect(await screen.findByRole('button', { name: 'Starting transfer' })).toBeDisabled();
});
