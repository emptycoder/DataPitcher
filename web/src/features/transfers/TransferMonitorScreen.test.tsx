import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { TransferMonitorScreen } from './TransferMonitorScreen';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const scheduler = { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() };
const jobId = '22222222-2222-4222-8222-222222222222';

function job(state: string, extra: Record<string, unknown> = {}) {
  return { state, rowsTransferred: 5, bytesTransferred: 1_024, totalRows: 5, currentTable: 'orders', failureDetail: undefined, ...extra };
}

function eventStream(events: string) {
  return new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(events)); controller.close(); } }));
}

function renderMonitor(request = vi.fn().mockResolvedValue(eventStream('')), clock = () => 2_000) {
  return render(<TransferMonitorScreen jobId={jobId} request={request} authentication={authentication} clock={clock} scheduler={scheduler} />);
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it('asks for a job id before loading transfer progress', () => {
  vi.stubGlobal('fetch', vi.fn());

  render(<TransferMonitorScreen jobId={null} request={vi.fn()} authentication={authentication} clock={() => 0} scheduler={scheduler} />);

  expect(screen.getByRole('status')).toHaveTextContent('Choose a transfer job to monitor.');
  expect(fetch).not.toHaveBeenCalled();
});

it('fetches a completed job at mount without opening a stream', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Succeeded')), { status: 200 })));
  const request = vi.fn();

  renderMonitor(request);

  expect(await screen.findByText('Transfer succeeded.')).toBeVisible();
  expect(screen.getByText('orders')).toBeVisible();
  expect(request).not.toHaveBeenCalled();
});

it('shows a determinate running transfer without inferring success from its row count', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Running')), { status: 200 })));

  renderMonitor();

  expect(await screen.findByRole('progressbar', { name: 'Transfer progress' })).toHaveAttribute('max', '5');
  expect(screen.getByText('running')).toBeVisible();
  expect(screen.queryByText('Transfer succeeded.')).toBeNull();
});

it('shows indeterminate activity when a running transfer has no total', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Running', { totalRows: undefined })), { status: 200 })));

  renderMonitor();

  expect(await screen.findByRole('progressbar', { name: 'Transfer activity' })).not.toHaveAttribute('value');
});

it('reduces live progress through explicit success', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Queued', { rowsTransferred: 0 })), { status: 200 })));
  const request = vi.fn().mockResolvedValue(eventStream('event: state\ndata: {"State":"preparing","RowsTransferred":1,"BytesTransferred":200}\n\nevent: state\ndata: {"State":"running","RowsTransferred":2,"BytesTransferred":400}\n\nevent: state\ndata: {"State":"verifying","RowsTransferred":5,"BytesTransferred":1024}\n\nevent: state\ndata: {"State":"succeeded","RowsTransferred":5,"BytesTransferred":1024}\n\n'));

  renderMonitor(request, () => 5_000);

  expect(await screen.findByText('Transfer succeeded.')).toBeVisible();
  expect(screen.getByLabelText('Rows transferred')).toHaveTextContent('5');
  expect(screen.getByLabelText('Elapsed time')).toHaveTextContent('0s');
});

it('renders verification failures as failures', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Verifying')), { status: 200 })));
  const request = vi.fn().mockResolvedValue(eventStream('event: state\ndata: {"State":"verificationfailed","RowsTransferred":5,"BytesTransferred":1024}\n\n'));

  renderMonitor(request);

  expect(await screen.findByRole('alert')).toHaveTextContent('Verification failed. This transfer did not succeed.');
  expect(screen.queryByText('Transfer succeeded.')).toBeNull();
});

it('renders unknown state without showing progress', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('waiting-for-magic', { totalRows: undefined, currentTable: undefined })), { status: 200 })));

  renderMonitor();

  expect(await screen.findByText('Transfer state is unknown.')).toBeVisible();
  expect(screen.queryByRole('progressbar')).toBeNull();
  expect(screen.getByText('Unknown', { selector: 'dd' })).toBeVisible();
  expect(screen.getByText('No table reported')).toBeVisible();
});

it('surfaces stream 403 responses as permanent authorization failures without retrying', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Queued')), { status: 200 })));
  const request = vi.fn().mockResolvedValue(new Response(null, { status: 403 }));

  renderMonitor(request);

  expect(await screen.findByRole('alert')).toHaveTextContent('Authorization failed permanently.');
  expect(request).toHaveBeenCalledOnce();
});

it('surfaces a forbidden initial fetch as a permanent authorization failure', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 403 })));

  renderMonitor();

  expect(await screen.findByRole('alert')).toHaveTextContent('Authorization failed permanently.');
});

it('surfaces other initial fetch failures', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })));

  renderMonitor();

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load transfer progress.');
});

it('aborts the event stream on unmount', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(job('Queued')), { status: 200 })));
  let signal: AbortSignal | undefined;
  const request = vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
    signal = init?.signal ?? undefined;
    return Promise.resolve(new Response(new ReadableStream({ start(controller) {
      signal?.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')));
    } })));
  });

  const monitor = renderMonitor(request);
  await waitFor(() => expect(request).toHaveBeenCalledOnce());
  monitor.unmount();

  expect(signal?.aborted).toBe(true);
});
