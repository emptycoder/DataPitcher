import { z } from 'zod';

export const JobEventState = z.enum([
  'draft', 'queued', 'preparing', 'running', 'pausing', 'paused', 'cancelling', 'cancelled', 'verifying', 'succeeded', 'failed', 'verificationfailed',
]);
export type JobEventState = z.infer<typeof JobEventState>;

export type JobView = Readonly<{
  state: JobEventState | 'unknown';
  rowsTransferred: number;
  bytesTransferred: number;
  totalRows: number | undefined;
  currentTable: string | undefined;
  failureDetail: string | undefined;
}>;

export type JobEventFrame = Readonly<{ event: string; data: string }>;
export type JobEvent = Readonly<{ type: 'event'; event: 'state' | 'progress'; state: JobEventState | 'unknown'; rowsTransferred: number; bytesTransferred: number }>;
export type JobEventProblem = Readonly<{ type: 'problem'; reason: 'malformed-payload' | 'unknown-event' | 'unauthorized' | 'forbidden' | 'request-failed' | 'missing-body' }>;
export type JobEventResult = JobEvent | JobEventProblem;
export type JobEventRequest = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
export type JobEventAuthentication = Readonly<{ getAccessToken: () => Promise<string | null> }>;

const payloadSchema = z.object({
  State: z.string(),
  RowsTransferred: z.number().int().nonnegative(),
  BytesTransferred: z.number().int().nonnegative(),
});

const validTargets: Readonly<Record<JobEventState, readonly JobEventState[]>> = {
  draft: ['queued', 'cancelling'],
  queued: ['preparing', 'cancelling'],
  preparing: ['running', 'pausing', 'cancelling', 'failed', 'queued'],
  running: ['pausing', 'cancelling', 'verifying', 'failed', 'queued'],
  pausing: ['paused', 'cancelling', 'failed', 'queued'],
  paused: ['queued', 'cancelling'],
  cancelling: ['cancelled', 'failed'],
  cancelled: [],
  verifying: ['succeeded', 'failed', 'verificationfailed'],
  succeeded: [],
  failed: [],
  verificationfailed: [],
};

const terminalStates = new Set<JobView['state']>(['cancelled', 'succeeded', 'failed', 'verificationfailed']);

export function parseJobEvent(frame: JobEventFrame): JobEventResult {
  if (frame.event !== 'state' && frame.event !== 'progress') return { type: 'problem', reason: 'unknown-event' };
  try {
    const parsed = payloadSchema.safeParse(JSON.parse(frame.data));
    if (!parsed.success) return { type: 'problem', reason: 'malformed-payload' };
    const state = JobEventState.safeParse(parsed.data.State);
    return {
      type: 'event',
      event: frame.event,
      state: state.success ? state.data : 'unknown',
      rowsTransferred: parsed.data.RowsTransferred,
      bytesTransferred: parsed.data.BytesTransferred,
    };
  } catch {
    return { type: 'problem', reason: 'malformed-payload' };
  }
}

export function isTerminalJobState(state: JobView['state']): boolean {
  return terminalStates.has(state);
}

export function reduceJobEvent(view: JobView, event: JobEvent): JobView {
  if (isTerminalJobState(view.state)) return view;
  if (view.state === 'unknown' || event.state === 'unknown' || !isLegalTransition(view.state, event.state)) return { ...view, state: 'unknown' };
  return { ...view, state: event.state, rowsTransferred: event.rowsTransferred, bytesTransferred: event.bytesTransferred };
}

export async function* streamJobEvents(jobId: string, request: JobEventRequest, authentication: JobEventAuthentication, signal?: AbortSignal): AsyncGenerator<JobEventResult> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    const token = await authentication.getAccessToken();
    if (!token) {
      yield { type: 'problem', reason: 'unauthorized' };
      return;
    }
    const response = await request(`/api/jobs/${jobId}/events`, { headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream' }, signal });
    if (response.status === 401 && attempt === 0) continue;
    if (response.status === 401) {
      yield { type: 'problem', reason: 'unauthorized' };
      return;
    }
    if (response.status === 403) {
      yield { type: 'problem', reason: 'forbidden' };
      return;
    }
    if (!response.ok) {
      yield { type: 'problem', reason: 'request-failed' };
      return;
    }
    yield* readJobEventResponse(response);
    return;
  }
}

async function* readJobEventResponse(response: Response): AsyncGenerator<JobEventResult> {
  if (!response.body) {
    yield { type: 'problem', reason: 'missing-body' };
    return;
  }
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let pending = '';
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) return;
      pending += decoder.decode(chunk.value, { stream: true });
      let boundary = pending.indexOf('\n\n');
      while (boundary >= 0) {
        const fields = Object.fromEntries(pending.slice(0, boundary).split('\n').map((line) => [line.slice(0, line.indexOf(':')), line.slice(line.indexOf(':') + 2)]));
        pending = pending.slice(boundary + 2);
        yield parseJobEvent({ event: String(fields.event), data: String(fields.data) });
        boundary = pending.indexOf('\n\n');
      }
    }
  } finally {
    reader.releaseLock();
  }
}

function isLegalTransition(from: JobEventState, to: JobEventState): boolean {
  return from === to || validTargets[from].includes(to);
}
