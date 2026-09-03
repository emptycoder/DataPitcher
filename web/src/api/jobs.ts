import { z } from 'zod';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { OperationReceiptSchema } from './connections';
import { requestJson } from './http';

export const jobStates = [
  'draft',
  'queued',
  'preparing',
  'running',
  'pausing',
  'paused',
  'cancelling',
  'cancelled',
  'verifying',
  'succeeded',
  'failed',
  'verificationfailed',
] as const;
export type JobState = (typeof jobStates)[number];

export function normalizeJobState(state: string): JobState | 'unknown' {
  const lower = state.toLowerCase();
  return (jobStates as readonly string[]).includes(lower) ? (lower as JobState) : 'unknown';
}

export const terminalStates: ReadonlySet<string> = new Set(['cancelled', 'succeeded', 'failed', 'verificationfailed']);
export function isTerminal(state: string) {
  return terminalStates.has(normalizeJobState(state));
}
export function isActive(state: string) {
  return !isTerminal(state) && normalizeJobState(state) !== 'unknown' && normalizeJobState(state) !== 'draft';
}

export const JobSchema = z.object({
  jobId: z.string(),
  planId: z.string(),
  state: z.string(),
  rowsTransferred: z.number(),
  bytesTransferred: z.number(),
  failureCode: z.string().nullable().optional(),
  failureDetail: z.string().nullable().optional(),
});
export type Job = z.infer<typeof JobSchema>;

export const JobSummarySchema = JobSchema.extend({ createdUtc: z.string(), updatedUtc: z.string() });
export type JobSummary = z.infer<typeof JobSummarySchema>;

export type JobCommand = 'Pause' | 'Resume' | 'Cancel';

const commandsByState: Readonly<Record<JobState, readonly JobCommand[]>> = {
  draft: ['Cancel'],
  queued: ['Cancel'],
  preparing: ['Pause', 'Cancel'],
  running: ['Pause', 'Cancel'],
  pausing: ['Cancel'],
  paused: ['Resume', 'Cancel'],
  cancelling: [],
  cancelled: [],
  verifying: [],
  succeeded: [],
  failed: [],
  verificationfailed: [],
};

export function legalCommands(state: string): readonly JobCommand[] {
  const normalized = normalizeJobState(state);
  return normalized === 'unknown' ? [] : commandsByState[normalized];
}

export const jobsApi = {
  list: (auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>('/api/jobs', auth, { signal }).then((data) =>
      z
        .array(JobSummarySchema)
        .parse(data)
        .toSorted((left, right) => right.createdUtc.localeCompare(left.createdUtc)),
    ),
  get: (jobId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/jobs/${jobId}`, auth, { signal }).then((data) => JobSchema.parse(data)),
  command: (jobId: string, command: JobCommand, auth: AuthenticationAdapter) =>
    requestJson<unknown>(`/api/jobs/${jobId}/commands`, auth, { method: 'POST', body: { command } }).then((data) => OperationReceiptSchema.parse(data)),
};

/* ------------------------------ Event stream ------------------------------ */

export const JobEventPayloadSchema = z.object({
  State: z.string(),
  RowsTransferred: z.number().int().nonnegative(),
  BytesTransferred: z.number().int().nonnegative(),
  Detail: z.string().nullable().optional(),
});

export type JobStreamEvent = Readonly<{ id: number; type: string; state: JobState | 'unknown'; rowsTransferred: number; bytesTransferred: number; receivedAt: number; detail?: string | null }>;
export type JobStreamStatus = 'connecting' | 'live' | 'reconnecting' | 'ended' | 'forbidden' | 'unauthorized' | 'cursor-expired';

export type JobStreamHandlers = Readonly<{
  onEvent: (event: JobStreamEvent) => void;
  onStatus: (status: JobStreamStatus) => void;
}>;

type ParserState = { line: string; fields: { id?: string; event?: string; data: string[] } };

function parseChunk(state: ParserState, chunk: string): readonly Readonly<{ id?: string; event?: string; data: string }>[] {
  const messages: Readonly<{ id?: string; event?: string; data: string }>[] = [];
  let buffer = state.line + chunk;
  let breakAt = buffer.indexOf('\n');
  while (breakAt >= 0) {
    const raw = buffer.slice(0, breakAt);
    buffer = buffer.slice(breakAt + 1);
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    if (line === '') {
      if (state.fields.data.length > 0) messages.push({ id: state.fields.id, event: state.fields.event, data: state.fields.data.join('\n') });
      state.fields = { data: [] };
    } else if (!line.startsWith(':')) {
      const separator = line.indexOf(':');
      const field = separator < 0 ? line : line.slice(0, separator);
      const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '');
      if (field === 'data') state.fields.data.push(value);
      else if (field === 'event') state.fields.event = value;
      else if (field === 'id') state.fields.id = value;
    }
    breakAt = buffer.indexOf('\n');
  }
  state.line = buffer;
  return messages;
}

/**
 * Streams job events with reconnect, Last-Event-ID resume, and monotonic de-duplication.
 * Returns a stop function. Stops automatically once a terminal state is observed.
 */
export function streamJobEvents(jobId: string, auth: AuthenticationAdapter, handlers: JobStreamHandlers): () => void {
  let stopped = false;
  let watermark = 0;
  let attempt = 0;
  let controller: AbortController | null = null;
  let timer: number | null = null;

  const stop = () => {
    stopped = true;
    controller?.abort();
    if (timer !== null) window.clearTimeout(timer);
  };

  const scheduleReconnect = () => {
    if (stopped) return;
    attempt += 1;
    handlers.onStatus('reconnecting');
    const delay = Math.min(10_000, 500 * 2 ** Math.min(attempt, 5)) + Math.random() * 300;
    timer = window.setTimeout(() => void connect(), delay);
  };

  const connect = async () => {
    if (stopped) return;
    handlers.onStatus(attempt === 0 ? 'connecting' : 'reconnecting');
    controller = new AbortController();
    try {
      const token = await auth.getAccessToken();
      if (!token) {
        handlers.onStatus('unauthorized');
        stop();
        return;
      }
      const headers: Record<string, string> = { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' };
      if (watermark > 0) headers['Last-Event-ID'] = String(watermark);
      const response = await fetch(`/api/jobs/${jobId}/events`, { headers, signal: controller.signal });
      if (response.status === 401) {
        handlers.onStatus('unauthorized');
        stop();
        return;
      }
      if (response.status === 403) {
        handlers.onStatus('forbidden');
        stop();
        return;
      }
      if (response.status === 409) {
        handlers.onStatus('cursor-expired');
        watermark = 0;
        scheduleReconnect();
        return;
      }
      if (!response.ok || !response.body) {
        scheduleReconnect();
        return;
      }
      attempt = 0;
      handlers.onStatus('live');
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      const parser: ParserState = { line: '', fields: { data: [] } };
      while (!stopped) {
        const next = await reader.read();
        if (next.done) break;
        for (const message of parseChunk(parser, decoder.decode(next.value, { stream: true }))) {
          const id = Number(message.id);
          if (!Number.isSafeInteger(id) || id <= watermark) continue;
          const parsed = JobEventPayloadSchema.safeParse(JSON.parse(message.data));
          if (!parsed.success) continue;
          watermark = id;
          const event: JobStreamEvent = {
            id,
            type: message.event ?? 'message',
            state: normalizeJobState(parsed.data.State),
            rowsTransferred: parsed.data.RowsTransferred,
            bytesTransferred: parsed.data.BytesTransferred,
            receivedAt: Date.now(),
            detail: parsed.data.Detail ?? null,
          };
          handlers.onEvent(event);
          if (isTerminal(event.state)) {
            handlers.onStatus('ended');
            stop();
            return;
          }
        }
      }
      if (!stopped) scheduleReconnect();
    } catch (error) {
      if (stopped || (error instanceof DOMException && error.name === 'AbortError')) return;
      scheduleReconnect();
    }
  };

  void connect();
  return stop;
}
