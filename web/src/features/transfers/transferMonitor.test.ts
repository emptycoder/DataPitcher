import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import type { JobSnapshot } from './transferMonitorModel';
import { createTransferMonitor } from './transferMonitor';

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
