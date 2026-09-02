import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { getJobEventsUrl, getJobUrl } from '../../api/generated/client';
import { TransferJobSnapshot } from '../../api/jobEventSchema';
import { consumeEventStream, isTerminalJobState, reduceJobEvent, TransferEventPayload, type JobSnapshot } from './transferMonitorModel';

export type TransferMonitorState = 'connecting' | 'connected' | 'disconnected' | 'unauthorized' | 'forbidden' | 'stopped';
export type TransferMonitorScheduler = Readonly<{ setTimeout: (work: () => void, delay: number) => unknown; clearTimeout: (handle: unknown) => void }>;
export type TransferMonitorRequest = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

type TransferMonitorOptions = Readonly<{
  job: JobSnapshot;
  request: TransferMonitorRequest;
  authentication: AuthenticationAdapter;
  cache: Readonly<{ set: (job: JobSnapshot) => void }>;
  clock: () => number;
  scheduler: TransferMonitorScheduler;
  onState?: (state: TransferMonitorState) => void;
}>;

export function createTransferMonitor(options: TransferMonitorOptions) {
  let job = options.job;
  let sampledAt = options.clock();
  let watermark = 0;
  let authenticationFailures = 0;
  let stopped = false;
  let controller: AbortController | undefined;
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  let timer: unknown;

  async function fetchJob(): Promise<JobSnapshot> {
    const token = await options.authentication.getAccessToken();
    if (!token) throw new StreamResponseError(401);
    const response = await options.request(getJobUrl(job.jobId), { headers: { Authorization: `Bearer ${token}` } });
    if (!response.ok) throw new StreamResponseError(response.status);
    return TransferJobSnapshot.parse(await response.json());
  }

  function scheduleReconnect(state: TransferMonitorState) {
    if (stopped || timer !== undefined) return;
    options.onState?.(state);
    timer = options.scheduler.setTimeout(() => {
      timer = undefined;
      void connect();
    }, 1_000);
  }

  function cleanup() {
    if (timer !== undefined) options.scheduler.clearTimeout(timer);
    timer = undefined;
    controller?.abort();
    controller = undefined;
    void reader?.cancel();
    reader = undefined;
  }

  function finish(state: TransferMonitorState) {
    stopped = true;
    cleanup();
    options.onState?.(state);
  }

  async function handleResponseStatus(status: number) {
    if (status === 403) return finish('forbidden');
    if (status === 401) {
      authenticationFailures += 1;
      if (authenticationFailures === 2) finish('unauthorized'); else scheduleReconnect('unauthorized');
      return;
    }
    scheduleReconnect('disconnected');
  }

  async function connect(): Promise<void> {
    if (stopped) return;
    options.onState?.('connecting');
    controller = new AbortController();
    try {
      const token = await options.authentication.getAccessToken();
      if (!token) return handleResponseStatus(401);
      const headers: Record<string, string> = { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' };
      if (watermark > 0) headers['Last-Event-ID'] = String(watermark);
      const response = await options.request(getJobEventsUrl(job.jobId), { headers, signal: controller.signal });
      if (!response.ok) return handleResponseStatus(response.status);
      authenticationFailures = 0;
      if (!response.body) return scheduleReconnect('disconnected');
      options.onState?.('connected');
      reader = response.body.getReader();
      const decoder = new TextDecoder();
      let parser = { line: '', event: {} };
      while (!stopped) {
        const next = await reader.read();
        if (next.done) break;
        const parsed = consumeEventStream(parser, decoder.decode(next.value, { stream: true }));
        parser = parsed.parser;
        for (const message of parsed.events) {
          if (!await applyEvent(message.id, message.data)) return;
        }
      }
      if (!stopped) scheduleReconnect('disconnected');
    } catch (error) {
      if (stopped || (error instanceof DOMException && error.name === 'AbortError')) return;
      if (error instanceof StreamResponseError) await handleResponseStatus(error.status); else scheduleReconnect('disconnected');
    } finally {
      reader = undefined;
      controller = undefined;
    }
  }

  async function applyEvent(identifier: string | undefined, data: string): Promise<boolean> {
    const id = Number(identifier);
    if (!Number.isSafeInteger(id) || id < 0 || id <= watermark) return true;
    let payload: TransferEventPayload;
    try {
      payload = TransferEventPayload.parse(JSON.parse(data));
    } catch {
      return true;
    }
    if (id > watermark + 1) {
      await reloadAfterGap(id);
      return false;
    }
    job = reduceJobEvent(job, payload, sampledAt, options.clock());
    sampledAt = options.clock();
    watermark = id;
    options.cache.set(job);
    if (isTerminalJobState(job.state)) {
      finish('stopped');
      return false;
    }
    return true;
  }

  async function reloadAfterGap(id: number) {
    try {
      job = await fetchJob();
      sampledAt = options.clock();
      watermark = id;
      options.cache.set(job);
      if (isTerminalJobState(job.state)) finish('stopped'); else scheduleReconnect('disconnected');
    } catch (error) {
      if (error instanceof StreamResponseError) await handleResponseStatus(error.status); else scheduleReconnect('disconnected');
    }
  }

  return {
    start: connect,
    stop: () => finish('stopped'),
  };
}

class StreamResponseError extends Error {
  constructor(readonly status: number) {
    super(`Stream request failed: ${status}`);
  }
}
