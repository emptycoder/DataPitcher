import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { routes } from '../../app/routes';
import { matchRoute } from '../../app/routeMatch';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { PermissionsProvider } from '../../auth/permissions';
import { PlanReview } from './PlanReview';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const planId = '11111111-1111-4111-8111-111111111111';
const sealedReview = {
  planId,
  version: 4,
  canonicalHash: 'A'.repeat(64),
  seal: { status: 'sealed', invalidationReasons: [] },
  totals: { included: 12, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 4096 },
  startPreconditions: [],
  tables: [{ source: { schema: 'sales', name: 'Orders' }, target: { schema: 'archive', name: 'Orders' }, state: 'Root', transferOrder: 1, included: 12, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 4096, columns: [] }],
  conflicts: [],
  cycles: [],
  warnings: [],
  blockers: [],
};

function renderReview() {
  return render(<AppProviders client={new QueryClient()}><PlanReview planId={planId} authentication={authentication} /></AppProviders>);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it('registers the plan review route with a plan identifier', () => {
  expect(matchRoute(`/plans/${planId}/review`, routes)?.route.label).toBe('Plan review');
});

it('shows loading while the review is being retrieved', () => {
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
  renderReview();

  expect(screen.getByRole('status')).toHaveTextContent('Loading plan review');
});

it('shows a load failure without inventing a plan', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ detail: 'Unavailable' }), { status: 500 })));
  renderReview();

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load plan review.');
});

it('renders sealed totals, mapped tables, and an available inclusion path', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).endsWith('/inclusion-paths')
    ? { table: 'sales.Orders', stableKey: 'Id=42', rootSelection: 'Open orders', steps: [{ relationship: 'Root selection', from: 'sales.Orders', to: 'sales.Orders', reason: 'Selected as a root row.' }] }
    : sealedReview), { status: 200 })));
  renderReview();

  expect(await screen.findByRole('region', { name: 'Overall totals' })).toHaveTextContent('12');
  expect(screen.getByText(/^Canonical hash:/)).toHaveTextContent('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA');
  expect(screen.getByText('Source database:')).toBeVisible();
  expect(screen.getByText('Target database:')).toBeVisible();
  expect(screen.getAllByText('unavailable in this review payload.')).toHaveLength(2);
  expect(screen.getByText('sales.Orders')).toBeVisible();
  expect(screen.getByText('archive.Orders')).toBeVisible();
  fireEvent.change(screen.getByRole('textbox', { name: 'Stable key for sales.Orders' }), { target: { value: 'Id=42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Explain inclusion' }));

  expect(await screen.findByText('Selected as a root row.')).toBeVisible();
});

it('marks every unsealed count as unknown and forbids transfer', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...sealedReview, canonicalHash: '', seal: { status: 'invalidated', invalidationReasons: [{ code: 'plan_not_sealed', message: 'This plan has not completed sealing.' }] } }), { status: 200 })));
  renderReview();

  expect(await screen.findByText('Unsealed — do not transfer.')).toBeVisible();
  expect(screen.getByText('Canonical hash: Unknown')).toBeVisible();
  expect(screen.getAllByText('Unknown').length).toBeGreaterThan(1);
});

it('keeps known zero totals distinct from unknown totals for an empty sealed plan', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...sealedReview, totals: { included: 0, plannedWrites: 0, inserts: 0, updates: 0, estimatedBytes: 0 }, tables: [] }), { status: 200 })));
  renderReview();

  expect(await screen.findByText('No tables were included in this plan.')).toBeVisible();
  expect(screen.getAllByText('0').length).toBeGreaterThan(1);
});

it('reports unavailable inclusion paths rather than inventing one', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).endsWith('/inclusion-paths') ? { detail: 'Not wired.' } : sealedReview), { status: String(input).endsWith('/inclusion-paths') ? 500 : 200 })));
  renderReview();

  fireEvent.change(await screen.findByRole('textbox', { name: 'Stable key for sales.Orders' }), { target: { value: 'Id=42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Explain inclusion' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Inclusion paths are unavailable from this server.');
});

it('shows inclusion loading while an explanation is being retrieved', async () => {
  let resolvePath: (response: Response) => void;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => String(input).endsWith('/inclusion-paths')
    ? new Promise<Response>((resolve) => { resolvePath = resolve; })
    : Promise.resolve(new Response(JSON.stringify(sealedReview), { status: 200 }))));
  renderReview();

  fireEvent.change(await screen.findByRole('textbox', { name: 'Stable key for sales.Orders' }), { target: { value: 'Id=42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Explain inclusion' }));

  expect(await screen.findByRole('button', { name: 'Loading explanation' })).toBeDisabled();
  resolvePath!(new Response(JSON.stringify({ table: 'sales.Orders', stableKey: 'Id=42', rootSelection: 'Open orders', steps: [] }), { status: 200 }));
});

it('hides the review only after verified permissions deny plan read access', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(String(input).endsWith('/effective-permissions') ? { principalId: 'operator-1', tenantId: 'tenant-1', permissions: [] } : sealedReview), { status: 200 })));
  render(<AppProviders client={new QueryClient()}><PermissionsProvider authentication={authentication}><PlanReview planId={planId} authentication={authentication} /></PermissionsProvider></AppProviders>);

  expect(await screen.findByRole('alert')).toHaveTextContent('You do not have permission to review this plan.');
});
