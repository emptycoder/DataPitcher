import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { routes } from '../../app/routes';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { PermissionsProvider } from '../../auth/permissions';
import { PlanSeal } from './PlanSeal';

const planId = '11111111-1111-4111-8111-111111111111';
const selection = { selectionId: '22222222-2222-4222-8222-222222222222', displayName: 'Open orders' };
const source = { connectionId: '33333333-3333-4333-8333-333333333333', displayName: 'Production source', providerId: 'sqlserver', health: 'Healthy', eTag: '"1"' };
const target = { connectionId: '44444444-4444-4444-8444-444444444444', displayName: 'Reporting TARGET', providerId: 'sqlserver', health: 'Healthy', eTag: '"1"' };
const unsealed = { planId, version: 1, canonicalHash: '', seal: { status: 'invalidated', invalidationReasons: [] }, totals: { included: 12, plannedWrites: 9 }, selection, source, target };
const sealed = { ...unsealed, seal: { status: 'sealed', invalidationReasons: [] } };
const receipt = { operationId: '55555555-5555-4555-8555-555555555555', state: 'queued', statusUri: '/api/operations/55555555-5555-4555-8555-555555555555', planId, jobId: null };
const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');

type Reply = Readonly<{ body: unknown; status?: number }>;
type ServerOptions = Readonly<{ reviews?: readonly Reply[]; save?: Reply; seal?: Reply; operation?: Reply; job?: Reply }>;

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function server({ reviews = [{ body: sealed }], save = { body: { planId, version: 2, canonicalHash: null, eTag: '"2"' } }, seal = { body: receipt, status: 202 }, operation = { body: { ...receipt, operation: 'plan-seal', state: 'succeeded', finished: true, failed: false, failureCode: null } }, job = { body: { ...receipt, jobId: '66666666-6666-4666-8666-666666666666' }, status: 202 } }: ServerOptions = {}) {
  let reviewIndex = 0;
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    if (url.endsWith('/review')) {
      const reply = reviews[Math.min(reviewIndex++, reviews.length - 1)]!;
      return json(reply.body, reply.status);
    }
    if (url === '/api/selections') return json({ selections: [selection] });
    if (url === '/api/connections') return json([source, target]);
    if (url === '/api/auth/effective-permissions') return json({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: [] });
    if (url === `/api/plans/${planId}` && method === 'PUT') return json(save.body, save.status);
    if (url.endsWith('/seal')) return json(seal.body, seal.status);
    if (url === `/api/operations/${receipt.operationId}`) return json(operation.body, operation.status);
    if (url.endsWith('/jobs')) return json(job.body, job.status);
    throw new Error(`Unexpected request: ${method} ${url}`);
  });
}

function renderPlan(fetch: ReturnType<typeof server>, verifiedPermissions = false) {
  vi.stubGlobal('fetch', fetch);
  const plan = <AppProviders client={new QueryClient()}><PlanSeal planId={planId} authentication={authentication} /></AppProviders>;
  return render(verifiedPermissions ? <PermissionsProvider authentication={authentication}>{plan}</PermissionsProvider> : plan);
}

async function confirmTransfer() {
  fireEvent.click(await screen.findByRole('button', { name: 'Start transfer' }));
  return screen.findByRole('alertdialog', { name: 'Confirm transfer' });
}

async function requestSeal() {
  const button = await screen.findByRole('button', { name: 'Seal plan' });
  await waitFor(() => expect(button).toBeEnabled());
  fireEvent.click(button);
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, '', '/');
});

it('associates a plan with a saved selection, source, and target before sealing', async () => {
  const fetch = server({ reviews: [{ body: { title: 'Plan was not found.' }, status: 404 }, { body: unsealed }] });
  renderPlan(fetch);

  await screen.findByRole('option', { name: 'Open orders' });
  fireEvent.change(screen.getByLabelText('Plan name'), { target: { value: 'Orders transfer' } });
  fireEvent.change(screen.getByLabelText('Operator note'), { target: { value: 'Production copy' } });
  fireEvent.change(await screen.findByLabelText('Saved selection'), { target: { value: selection.selectionId } });
  fireEvent.change(screen.getByLabelText('Source database'), { target: { value: source.connectionId } });
  fireEvent.change(screen.getByLabelText('TARGET database'), { target: { value: target.connectionId } });
  fireEvent.submit(screen.getByRole('form', { name: 'Plan association' }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(`/api/plans/${planId}`, expect.objectContaining({ method: 'PUT' })));
  expect(JSON.parse(String(fetch.mock.calls.find(([url, init]) => String(url) === `/api/plans/${planId}` && init?.method === 'PUT')?.[1]?.body))).toMatchObject({ displayName: 'Orders transfer', operatorNote: 'Production copy', selectionId: selection.selectionId, sourceConnectionId: source.connectionId, targetConnectionId: target.connectionId });
  expect(await screen.findByRole('button', { name: 'Seal plan' })).toBeEnabled();
  fireEvent.submit(screen.getByRole('form', { name: 'Plan association' }));
  await waitFor(() => expect(fetch.mock.calls.filter(([url, init]) => String(url) === `/api/plans/${planId}` && init?.method === 'PUT')).toHaveLength(2));
  expect(JSON.parse(String(fetch.mock.calls.filter(([url, init]) => String(url) === `/api/plans/${planId}` && init?.method === 'PUT')[1]![1]?.body))).toMatchObject({ ifMatch: '"2"' });
});

it('shows sealing as pending until the operation resolves', async () => {
  const fetch = server({ reviews: [{ body: unsealed }], operation: { body: { ...receipt, operation: 'plan-seal', state: 'running', finished: false, failed: false, failureCode: null } } });
  renderPlan(fetch);

  await requestSeal();

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(`/api/plans/${planId}/seal`, expect.objectContaining({ method: 'POST' })));
  expect(await screen.findByText('Sealing in progress.')).toBeVisible();
  await new Promise((resolve) => window.setTimeout(resolve, 1000));
  await waitFor(() => expect(fetch.mock.calls.filter(([url]) => String(url) === `/api/operations/${receipt.operationId}`)).toHaveLength(2));
  expect(screen.queryByRole('button', { name: 'Start transfer' })).toBeNull();
});

it('allows confirmation after a successfully sealed plan is verified by review', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }, { body: sealed }] }));

  await requestSeal();

  expect(await screen.findByText('Plan is sealed.')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Start transfer' })).toBeEnabled();
});

it('presents sealing failure and does not offer start', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }], operation: { body: { ...receipt, operation: 'plan-seal', state: 'failed', finished: true, failed: true, failureCode: 'closure_failed' } } }));

  await requestSeal();

  expect(await screen.findByText('Sealing failed: closure_failed.')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Start transfer' })).toBeNull();
});

it('presents a sealing request failure', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }], seal: { body: { title: 'Unavailable' }, status: 500 } }));

  await requestSeal();

  expect(await screen.findByText('The service is unavailable. Try again.')).toBeVisible();
});

it('presents an operation status request failure', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }], operation: { body: { title: 'Unavailable' }, status: 500 } }));

  await requestSeal();

  expect(await screen.findByText('The service is unavailable. Try again.')).toBeVisible();
});

it('presents an unknown seal state without offering start', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }], operation: { body: { ...receipt, operation: 'plan-seal', state: 'unknown', finished: true, failed: false, failureCode: null } } }));

  await requestSeal();

  expect(await screen.findByText('Seal status is unknown.')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Start transfer' })).toBeNull();
});

it('presents an unknown seal state when completion cannot be verified by review', async () => {
  renderPlan(server({ reviews: [{ body: unsealed }, { body: unsealed }] }));

  await requestSeal();

  expect(await screen.findByText('Seal status is unknown.')).toBeVisible();
});

it('disables actions when verified permissions are absent', async () => {
  renderPlan(server(), true);

  await waitFor(() => expect(screen.getByRole('button', { name: 'Save plan' })).toBeDisabled());
  expect(screen.getByRole('button', { name: 'Seal plan' })).toBeDisabled();
  expect(screen.queryByRole('button', { name: 'Start transfer' })).toBeNull();
});

it('presents a plan review failure', async () => {
  renderPlan(server({ reviews: [{ body: { title: 'Unavailable' }, status: 500 }] }));

  expect(await screen.findByText('The service is unavailable. Try again.')).toBeVisible();
});

it('presents a plan save failure', async () => {
  renderPlan(server({ save: { body: { title: 'Unavailable' }, status: 500 } }));

  fireEvent.submit(await screen.findByRole('form', { name: 'Plan association' }));

  expect(await screen.findByText('The service is unavailable. Try again.')).toBeVisible();
});

it('shows saving while association is in flight', async () => {
  let resolveSave: (response: Response) => void = () => undefined;
  const base = server();
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => String(input) === `/api/plans/${planId}` ? new Promise<Response>((resolve) => { resolveSave = resolve; }) : base(input, init));
  renderPlan(fetch);

  fireEvent.submit(await screen.findByRole('form', { name: 'Plan association' }));

  expect(await screen.findByRole('button', { name: 'Saving plan' })).toBeDisabled();
  resolveSave(json({ planId, version: 2, canonicalHash: null, eTag: '"2"' }));
  expect(await screen.findByRole('button', { name: 'Save plan' })).toBeEnabled();
});

it('requires confirmation showing source, target, and planned rows before start', async () => {
  const fetch = server();
  renderPlan(fetch);

  const confirmation = await confirmTransfer();

  expect(confirmation).toHaveTextContent('Source database: Production source');
  expect(confirmation).toHaveTextContent('TARGET DATABASE: Reporting TARGET');
  expect(confirmation).toHaveTextContent('Total planned rows: 9');
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith('/jobs'))).toBe(false);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(screen.queryByRole('alertdialog', { name: 'Confirm transfer' })).toBeNull();
});

it('shows the unsealed conflict without retrying a start', async () => {
  const fetch = server({ job: { body: { title: 'Plan must be sealed before starting a job.' }, status: 409 } });
  renderPlan(fetch);

  await confirmTransfer();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm start transfer' }));

  expect(await screen.findByText('The plan must be sealed before starting a transfer.')).toBeVisible();
  await waitFor(() => expect(fetch.mock.calls.filter(([url]) => String(url).endsWith('/jobs'))).toHaveLength(1));
});

it('prevents a double submit while a start is in flight', async () => {
  let resolveJob: (response: Response) => void = () => undefined;
  const base = server({ job: { body: { ...receipt, jobId: '66666666-6666-4666-8666-666666666666' } } });
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => String(input).endsWith('/jobs') ? new Promise<Response>((resolve) => { resolveJob = resolve; }) : base(input, init));
  vi.stubGlobal('crypto', { randomUUID: vi.fn(() => 'start-key') });
  renderPlan(fetch);

  await confirmTransfer();
  const start = screen.getByRole('button', { name: 'Confirm start transfer' });
  fireEvent.click(start);
  fireEvent.click(start);

  await waitFor(() => expect(fetch.mock.calls.filter(([url]) => String(url).endsWith('/jobs'))).toHaveLength(1));
  expect(await screen.findByRole('button', { name: 'Starting transfer' })).toBeDisabled();
  expect(new Headers(fetch.mock.calls.find(([url]) => String(url).endsWith('/jobs'))![1]?.headers).get('Idempotency-Key')).toBe('start-key');
  resolveJob(json({ ...receipt, jobId: '66666666-6666-4666-8666-666666666666' }, 202));
  await waitFor(() => expect(window.location.pathname).toBe('/transfer-monitor/66666666-6666-4666-8666-666666666666'));
});

it('shows a start failure', async () => {
  renderPlan(server({ job: { body: { title: 'Unavailable' }, status: 500 } }));

  await confirmTransfer();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm start transfer' }));

  expect(await screen.findByText('The service is unavailable. Try again.')).toBeVisible();
});

it('navigates to the transfer monitor after starting', async () => {
  renderPlan(server());

  await confirmTransfer();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm start transfer' }));

  await waitFor(() => expect(window.location.pathname).toBe('/transfer-monitor/66666666-6666-4666-8666-666666666666'));
});

it('cleans up polling when unmounted', async () => {
  const clearInterval = vi.spyOn(window, 'clearInterval');
  const view = renderPlan(server({ reviews: [{ body: unsealed }], operation: { body: { ...receipt, operation: 'plan-seal', state: 'running', finished: false, failed: false, failureCode: null } } }));

  await requestSeal();
  await screen.findByText('Sealing in progress.');
  view.unmount();

  expect(clearInterval).toHaveBeenCalled();
});

it('registers the plan sealing route', () => {
  const route = routes.find((entry) => entry.path === '/plans/:planId/seal')!;
  expect(route.render({ planId }, { authentication }).props.planId).toBe(planId);
});
