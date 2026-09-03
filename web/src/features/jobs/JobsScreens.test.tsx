import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { PermissionsProvider } from '../../auth/permissions';
import { HttpError } from '../../api/http';
import { AppProviders } from '../../app/AppProviders';
import { routes } from '../../app/routes';
import { JobDetailScreen, JobsListScreen, jobStatePresentation, legalJobCommands, requestErrorMessage } from './JobsScreens';

const jobId = '22222222-2222-4222-8222-222222222222';
const planId = '11111111-1111-4111-8111-111111111111';
const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');

function job(state: string) {
  return { jobId, planId, state, rowsTransferred: 3, bytesTransferred: 1024 };
}

function renderDetail(state: string, request = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(init?.method === 'POST' ? { operationId: jobId } : job(state)), { status: init?.method === 'POST' ? 202 : 200 }))) {
  vi.stubGlobal('fetch', request);
  render(<AppProviders client={new QueryClient()}><JobDetailScreen jobId={jobId} authentication={authentication} /></AppProviders>);
  return request;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, '', '/');
});

describe('transfer jobs', () => {
  it.each([
    ['Draft', ['Cancel']], ['Queued', ['Cancel']], ['Preparing', ['Pause', 'Resume', 'Cancel']], ['Running', ['Pause', 'Resume', 'Cancel']],
    ['Pausing', ['Resume', 'Cancel']], ['Paused', ['Resume', 'Cancel']], ['Cancelling', []], ['Cancelled', []], ['Verifying', []],
    ['Succeeded', []], ['Failed', []], ['VerificationFailed', []], ['Unexpected', []],
  ])('allows only legal commands from %s', (state, commands) => {
    expect(legalJobCommands(state)).toEqual(commands);
  });

  it('renders verification failure as failure and an unrecognised state as unknown', async () => {
    renderDetail('VerificationFailed');
    expect(await screen.findByText('Verification failed. This transfer did not succeed.')).toBeVisible();
    expect(screen.getByRole('status')).toHaveAttribute('data-tone', 'danger');
    expect(screen.queryByText('Transfer succeeded')).toBeNull();
    cleanup();

    renderDetail('Unexpected');
    expect(await screen.findByText(/Job state is unknown/)).toBeVisible();
    expect(screen.getByRole('status')).toHaveAttribute('data-tone', 'neutral');
  });

  it('shows available job data, links a running job to its live monitor, and sends pause once', async () => {
    const request = renderDetail('Running');
    expect(await screen.findByRole('heading', { name: 'Transfer job' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Monitor live transfer' })).toHaveAttribute('href', `/transfer-monitor/${jobId}`);
    expect(screen.getByRole('link', { name: planId })).toHaveAttribute('href', `/plan-review/${planId}`);
    expect(screen.getAllByText('Not available from the job API.')).toHaveLength(3);
    fireEvent.click(screen.getByRole('button', { name: 'Pause transfer' }));
    await vi.waitFor(() => expect(request).toHaveBeenCalledWith(`/api/jobs/${jobId}/commands`, expect.objectContaining({ method: 'POST', body: '{"command":"Pause"}' })));
  });

  it('requires cancellation confirmation and prevents a pending command from being submitted twice', async () => {
    let releaseCommand: (response: Response) => void;
    const request = vi.fn((_input: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST'
      ? new Promise<Response>((resolve) => { releaseCommand = resolve; })
      : Promise.resolve(new Response(JSON.stringify(job('Running')), { status: 200 })));
    const confirm = vi.fn().mockReturnValueOnce(false).mockReturnValue(true);
    vi.stubGlobal('confirm', confirm);
    renderDetail('Running', request);
    await screen.findByRole('button', { name: 'Cancel transfer' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel transfer' }));
    expect(request).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel transfer' }));
    expect(confirm).toHaveBeenLastCalledWith('Cancelling a transfer leaves partially copied data. Cancel this transfer?');
    const sending = await screen.findAllByRole('button', { name: 'Sending command' });
    expect(sending).toHaveLength(3);
    sending.forEach((button) => expect(button).toBeDisabled());
    fireEvent.click(sending[0]!);
    expect(request).toHaveBeenCalledTimes(2);
    releaseCommand!(new Response(JSON.stringify({ operationId: jobId }), { status: 202 }));
  });

  it('shows a command error and hides commands only after verified permissions deny them', async () => {
    const failingCommand = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(init?.method === 'POST' ? {} : job('Running')), { status: init?.method === 'POST' ? 409 : 200 }));
    renderDetail('Running', failingCommand);
    await screen.findByRole('button', { name: 'Pause transfer' });
    fireEvent.click(screen.getByRole('button', { name: 'Pause transfer' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This job changed before the request could be completed.');
    cleanup();

    let resolvePermissions: (response: Response) => void;
    const denied = vi.fn((input: RequestInfo | URL) => String(input).includes('effective-permissions')
      ? new Promise<Response>((resolve) => { resolvePermissions = resolve; })
      : Promise.resolve(new Response(JSON.stringify(job('Running')), { status: 200 })));
    vi.stubGlobal('fetch', denied);
    render(<AppProviders client={new QueryClient()}><PermissionsProvider authentication={authentication}><JobDetailScreen jobId={jobId} authentication={authentication} /></PermissionsProvider></AppProviders>);
    expect(await screen.findByRole('button', { name: 'Pause transfer' })).toBeVisible();
    resolvePermissions!(new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: [] }), { status: 200 }));
    await vi.waitFor(() => expect(screen.queryByRole('button', { name: 'Pause transfer' })).toBeNull());
  });

  it.each([
    [new HttpError(401, null), 'Sign in to access this job.'], [new HttpError(403, null), 'You do not have permission to access this job.'],
    [new HttpError(404, null), 'This job was not found.'], [new HttpError(409, null), 'This job changed before the request could be completed.'],
    [new HttpError(503, null), 'The job service is unavailable. Try again.'], [new HttpError(418, null), 'The job request failed.'],
    [new Error('offline'), 'The job service could not be reached.'],
  ])('distinguishes job request failures', (error, message) => {
    expect(requestErrorMessage(error)).toBe(message);
  });

  it('shows a job load error', async () => {
    renderDetail('Running', vi.fn(async () => new Response(JSON.stringify({}), { status: 404 })));
    expect(await screen.findByRole('alert')).toHaveTextContent('This job was not found.');
  });

  it('honestly explains unavailable listing and opens a supplied job identifier', () => {
    render(<JobsListScreen />);
    expect(screen.getByRole('alert')).toHaveTextContent('GET /api/jobs');
    const form = screen.getByRole('form', { name: 'Open transfer job' });
    fireEvent.submit(form);
    expect(window.location.pathname).toBe('/');
    fireEvent.change(screen.getByRole('textbox', { name: 'Job ID' }), { target: { value: ` ${jobId} ` } });
    fireEvent.submit(form);
    expect(window.location.pathname).toBe(`/jobs/${jobId}`);
  });

  it('registers the optional job route for its list and detail screens', async () => {
    const route = routes.find((entry) => entry.path === '/jobs/:jobId?')!;
    expect(route).toBeDefined();
    render(<AppProviders client={new QueryClient()}>{route.render({}, { authentication })}</AppProviders>);
    expect(screen.getByRole('region', { name: 'Transfer jobs' })).toBeVisible();
    cleanup();

    const request = vi.fn(async () => new Response(JSON.stringify(job('Paused')), { status: 200 }));
    vi.stubGlobal('fetch', request);
    render(<AppProviders client={new QueryClient()}>{route.render({ jobId }, { authentication })}</AppProviders>);
    expect(await screen.findByRole('heading', { name: 'Transfer job' })).toBeVisible();
  });

  it.each([
    ['Draft', 'Draft', false], ['Queued', 'Queued', false], ['Preparing', 'Preparing', false], ['Running', 'Running', false],
    ['Pausing', 'Pausing', false], ['Paused', 'Paused', false], ['Cancelling', 'Cancelling', false], ['Cancelled', 'Cancelled', false],
    ['Verifying', 'Verifying', false], ['Succeeded', 'Succeeded', false], ['Failed', 'Failed', true], ['VerificationFailed', 'Verification failed', true],
  ])('presents %s without inferring success', (state, label, failure) => {
    expect(jobStatePresentation(state)).toMatchObject({ label, failure, unknown: false });
  });
});
