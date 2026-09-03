import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import type { JobSnapshot } from './transferMonitorModel';
import { createTransferMonitor, fetchTransferJob } from './transferMonitor';

const job: JobSnapshot = {
  jobId: '22222222-2222-4222-8222-222222222222',
  planId: '11111111-1111-4111-8111-111111111111',
  state: 'running',
  rowsTransferred: 2,
  bytesTransferred: 100,
  tableProgress: [],
};

function eventStream(text: string) {
  return new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(text)); controller.close(); } }), { status: 200 });
}

function scheduler() {
  let work: (() => void) | undefined;
  return {
    setTimeout: vi.fn((callback: () => void) => { work = callback; return 1; }),
    clearTimeout: vi.fn(),
    run: () => work?.(),
  };
}

it('drops duplicate events and reloads the canonical job on a sequence gap', async () => {
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 1\ndata: {"State":"running","RowsTransferred":4,"BytesTransferred":200}\n\nid: 1\ndata: {"State":"running","RowsTransferred":5,"BytesTransferred":300}\n\nid: 3\ndata: {"State":"verifying","RowsTransferred":6,"BytesTransferred":400}\n\n'))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...job, state: 'verifying', rowsTransferred: 6, bytesTransferred: 400 }), { status: 200 }));
  const cache = { set: vi.fn() };
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache,
    clock: () => 2_000,
    scheduler: timers,
  });

  await monitor.start();

  expect(cache.set).toHaveBeenCalledTimes(2);
  expect(cache.set.mock.calls[0]![0]).toMatchObject({ rowsTransferred: 4, bytesTransferred: 200 });
  expect(cache.set.mock.calls[1]![0]).toMatchObject({ state: 'verifying', rowsTransferred: 6 });
  expect(request).toHaveBeenNthCalledWith(2, `/api/jobs/${job.jobId}`, expect.objectContaining({ headers: { Authorization: 'Bearer memory-token' } }));
  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('reacquires a token once after 401 and permanently stops on a second 401', async () => {
  const request = vi.fn().mockResolvedValue(new Response(null, { status: 401 }));
  const authentication = { getPrincipal: vi.fn(), getAccessToken: vi.fn().mockResolvedValue('renewed-token'), signOut: vi.fn() };
  const timers = scheduler();
  const states: string[] = [];
  const monitor = createTransferMonitor({ job, request, authentication, cache: { set: vi.fn() }, clock: () => 0, scheduler: timers, onState: (state) => states.push(state) });

  await monitor.start();
  timers.run();
  await Promise.resolve();

  expect(authentication.getAccessToken).toHaveBeenCalledTimes(2);
  expect(states).toContain('unauthorized');
  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('permanently stops without retrying when the stream is forbidden', async () => {
  const timers = scheduler();
  const states: string[] = [];
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockResolvedValue(new Response(null, { status: 403 })),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
    onState: (state) => states.push(state),
  });

  await monitor.start();

  expect(states).toContain('forbidden');
  expect(timers.setTimeout).not.toHaveBeenCalled();
});

it('rejects an unauthenticated canonical job fetch without sending a request', async () => {
  const request = vi.fn();
  const authentication = { getPrincipal: vi.fn(), getAccessToken: vi.fn().mockResolvedValue(null), signOut: vi.fn() };

  await expect(fetchTransferJob(job.jobId, request, authentication)).rejects.toMatchObject({ status: 401 });
  expect(request).not.toHaveBeenCalled();
});

it('retries monitoring when no access token is available yet', async () => {
  const timers = scheduler();
  const request = vi.fn();
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: { getPrincipal: vi.fn(), getAccessToken: vi.fn().mockResolvedValue(null), signOut: vi.fn() },
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(request).not.toHaveBeenCalled();
  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('resumes a disconnected stream from its last processed event', async () => {
  const timers = scheduler();
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 1\ndata: {"State":"running","RowsTransferred":3,"BytesTransferred":150}\n\n'))
    .mockResolvedValueOnce(eventStream(''));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();
  timers.run();
  await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(2));

  expect(new Headers(request.mock.calls[1]![1]?.headers).get('Last-Event-ID')).toBe('1');
});

it('reconnects when a successful stream response has no body', async () => {
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockResolvedValue(new Response(null, { status: 200 })),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('reconnects after a non-authorization stream response failure', async () => {
  const timers = scheduler();
  const states: string[] = [];
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockResolvedValue(new Response(null, { status: 500 })),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
    onState: (state) => states.push(state),
  });

  await monitor.start();

  expect(states).toContain('disconnected');
  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('ignores malformed progress events without replacing the displayed job', async () => {
  const timers = scheduler();
  const cache = { set: vi.fn() };
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockResolvedValue(eventStream('id: 1\ndata: not-json\n\n')),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache,
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(cache.set).not.toHaveBeenCalled();
  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('stops a verification failure without reconnecting', async () => {
  const timers = scheduler();
  const states: string[] = [];
  const cache = { set: vi.fn() };
  const monitor = createTransferMonitor({
    job: { ...job, state: 'verifying' },
    request: vi.fn().mockResolvedValue(eventStream('id: 1\ndata: {"State":"verificationfailed","RowsTransferred":2,"BytesTransferred":100}\n\n')),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache,
    clock: () => 0,
    scheduler: timers,
    onState: (state) => states.push(state),
  });

  await monitor.start();

  expect(cache.set).toHaveBeenCalledWith(expect.objectContaining({ state: 'verificationfailed' }));
  expect(states).toContain('stopped');
  expect(timers.setTimeout).not.toHaveBeenCalled();
});

it('stops a pending reconnect and does not reopen the stream', async () => {
  const timers = scheduler();
  const request = vi.fn().mockResolvedValue(eventStream(''));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();
  monitor.stop();
  timers.run();
  await Promise.resolve();

  expect(timers.clearTimeout).toHaveBeenCalledWith(1);
  expect(request).toHaveBeenCalledOnce();
});

it('permanently stops when a sequence-gap refresh is forbidden', async () => {
  const timers = scheduler();
  const states: string[] = [];
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 2\ndata: {"State":"verifying","RowsTransferred":2,"BytesTransferred":100}\n\n'))
    .mockResolvedValueOnce(new Response(null, { status: 403 }));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
    onState: (state) => states.push(state),
  });

  await monitor.start();

  expect(states).toContain('forbidden');
  expect(timers.setTimeout).not.toHaveBeenCalled();
  expect(request).toHaveBeenNthCalledWith(2, `/api/jobs/${job.jobId}`, expect.anything());
});

it('reconnects when a sequence-gap refresh has no HTTP response', async () => {
  const timers = scheduler();
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 2\ndata: {"State":"verifying","RowsTransferred":2,"BytesTransferred":100}\n\n'))
    .mockRejectedValueOnce(new Error('network disconnected'));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('stops when the canonical sequence-gap refresh reaches a terminal state', async () => {
  const timers = scheduler();
  const states: string[] = [];
  const cache = { set: vi.fn() };
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 2\ndata: {"State":"verifying","RowsTransferred":2,"BytesTransferred":100}\n\n'))
    .mockResolvedValueOnce(new Response(JSON.stringify({ ...job, state: 'verificationfailed' }), { status: 200 }));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache,
    clock: () => 0,
    scheduler: timers,
    onState: (state) => states.push(state),
  });

  await monitor.start();

  expect(cache.set).toHaveBeenCalledWith(expect.objectContaining({ state: 'verificationfailed' }));
  expect(states).toContain('stopped');
  expect(timers.setTimeout).not.toHaveBeenCalled();
});

it('cancels an open stream when monitoring stops', async () => {
  let cancelled = false;
  const request = vi.fn().mockResolvedValue(new Response(new ReadableStream({ cancel() { cancelled = true; } }), { status: 200 }));
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: scheduler(),
  });

  const started = monitor.start();
  await vi.waitFor(() => expect(request).toHaveBeenCalledOnce());
  monitor.stop();
  await started;

  expect(cancelled).toBe(true);
});

it('does not reconnect when a pending stream request fails after stopping', async () => {
  let rejectRequest!: (error: Error) => void;
  const request = vi.fn(() => new Promise<Response>((_resolve, reject) => { rejectRequest = reject; }));
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  const started = monitor.start();
  await vi.waitFor(() => expect(request).toHaveBeenCalledOnce());
  monitor.stop();
  rejectRequest(new Error('request stopped'));
  await started;

  expect(timers.setTimeout).not.toHaveBeenCalled();
});

it('does not reconnect an aborted stream request', async () => {
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockRejectedValue(new DOMException('Aborted', 'AbortError')),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(timers.setTimeout).not.toHaveBeenCalled();
});

it('reconnects an interrupted stream request', async () => {
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request: vi.fn().mockRejectedValue(new DOMException('Disconnected', 'NetworkError')),
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  await monitor.start();

  expect(timers.setTimeout).toHaveBeenCalledOnce();
});

it('does not reconnect after stopping during a failed sequence-gap refresh', async () => {
  let rejectRefresh!: (error: Error) => void;
  const request = vi.fn()
    .mockResolvedValueOnce(eventStream('id: 2\ndata: {"State":"verifying","RowsTransferred":2,"BytesTransferred":100}\n\n'))
    .mockImplementationOnce(() => new Promise<Response>((_resolve, reject) => { rejectRefresh = reject; }));
  const timers = scheduler();
  const monitor = createTransferMonitor({
    job,
    request,
    authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'),
    cache: { set: vi.fn() },
    clock: () => 0,
    scheduler: timers,
  });

  const started = monitor.start();
  await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(2));
  monitor.stop();
  rejectRefresh(new Error('refresh stopped'));
  await started;

  expect(timers.setTimeout).not.toHaveBeenCalled();
});
