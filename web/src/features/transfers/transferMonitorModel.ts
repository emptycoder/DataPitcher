import { TransferEventPayload, type TableProgress, type TransferJobSnapshot } from '../../api/jobEventSchema';

export { TransferEventPayload } from '../../api/jobEventSchema';
export type JobSnapshot = TransferJobSnapshot & Readonly<{ bytesPerSecond?: number }>;

type EventFields = Readonly<{ id?: string; event?: string; data?: readonly string[]; retry?: number }>;
export type EventStreamParser = Readonly<{ line: string; event: EventFields }>;
export type EventStreamEvent = Readonly<{ id?: string; event?: string; data: string; retry?: number }>;

export function consumeEventStream(parser: EventStreamParser, chunk: string): Readonly<{ parser: EventStreamParser; events: readonly EventStreamEvent[] }> {
  let line = parser.line + chunk;
  let event = parser.event;
  const events: EventStreamEvent[] = [];
  let breakAt = line.indexOf('\n');
  while (breakAt >= 0) {
    const rawLine = line.slice(0, breakAt);
    line = line.slice(breakAt + 1);
    const parsed = processLine(rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine, event);
    event = parsed.event;
    if (parsed.message) events.push(parsed.message);
    breakAt = line.indexOf('\n');
  }
  return { parser: { line, event }, events };
}

function processLine(line: string, event: EventFields): Readonly<{ event: EventFields; message?: EventStreamEvent }> {
  if (line === '') {
    if (!event.data?.length) return { event: {} };
    return { event: {}, message: { id: event.id, event: event.event, data: event.data.join('\n'), retry: event.retry } };
  }
  if (line.startsWith(':')) return { event };
  const separator = line.indexOf(':');
  const field = separator < 0 ? line : line.slice(0, separator);
  const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '');
  if (field === 'data') return { event: { ...event, data: [...(event.data ?? []), value] } };
  if (field === 'event') return { event: { ...event, event: value } };
  if (field === 'id' && !value.includes('\0')) return { event: { ...event, id: value } };
  if (field === 'retry' && /^\d+$/.test(value)) return { event: { ...event, retry: Number(value) } };
  return { event };
}

export function reduceJobEvent(job: JobSnapshot, event: TransferEventPayload, previousAt: number, now: number): JobSnapshot {
  const elapsed = now - previousAt;
  const bytesPerSecond = elapsed > 0 ? Math.max(0, event.BytesTransferred - job.bytesTransferred) * 1_000 / elapsed : 0;
  return {
    ...job,
    state: event.State,
    rowsTransferred: event.RowsTransferred,
    bytesTransferred: event.BytesTransferred,
    tableProgress: event.TableProgress ?? job.tableProgress,
    bytesPerSecond,
  };
}

export function presentationForJob(job: JobSnapshot): Readonly<{ label: string; successful: boolean }> {
  if (job.state === 'succeeded') return { label: 'Transfer succeeded', successful: true };
  if (job.state === 'verificationfailed') return { label: 'Verification failed', successful: false };
  return { label: job.state, successful: false };
}

export function isTerminalJobState(state: JobSnapshot['state']) {
  return state === 'succeeded' || state === 'failed' || state === 'verificationfailed' || state === 'cancelled';
}

export function tableProgressLabel(progress: TableProgress) {
  return `${progress.table}: ${progress.rowsTransferred.toLocaleString('en-US')} rows, ${progress.bytesTransferred.toLocaleString('en-US')} bytes`;
}
