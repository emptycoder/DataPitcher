import { expect, it } from 'vitest';
import {
  consumeEventStream,
  presentationForJob,
  reduceJobEvent,
  tableProgressLabel,
  TransferEventPayload,
  type JobSnapshot,
} from './transferMonitorModel';

const job: JobSnapshot = {
  jobId: '22222222-2222-4222-8222-222222222222',
  planId: '11111111-1111-4111-8111-111111111111',
  state: 'running',
  rowsTransferred: 2,
  bytesTransferred: 100,
  tableProgress: [],
};

it('preserves a partial event-stream line until it is complete', () => {
  const first = consumeEventStream({ line: '', event: {} }, 'id: 3\ndata: {"State":"run');
  const second = consumeEventStream(first.parser, 'ning","RowsTransferred":4,"BytesTransferred":200}\n\n');

  expect(first.events).toEqual([]);
  expect(second.events).toEqual([{ id: '3', data: '{"State":"running","RowsTransferred":4,"BytesTransferred":200}' }]);
});

it('validates lowercase event states before reducing them into job progress', () => {
  const event = TransferEventPayload.parse({ State: 'running', RowsTransferred: 4, BytesTransferred: 200 });

  expect(reduceJobEvent(job, event, 1_000, 2_000)).toEqual({
    ...job,
    rowsTransferred: 4,
    bytesTransferred: 200,
    state: 'running',
    bytesPerSecond: 100,
  });
  expect(() => TransferEventPayload.parse({ State: 'Running', RowsTransferred: 4, BytesTransferred: 200 })).toThrow();
});

it('never describes a verification failure as a successful transfer', () => {
  expect(presentationForJob({ ...job, state: 'verificationfailed' })).toEqual({ label: 'Verification failed', successful: false });
});

it('preserves valid retry guidance and ignores unknown event-stream fields', () => {
  const parsed = consumeEventStream({ line: '', event: {} }, 'id: 4\r\nretry: 250\r\nunsupported: value\r\ndata: {}\r\n\r\n');

  expect(parsed.events).toEqual([{ id: '4', data: '{}', retry: 250 }]);
});

it('ignores empty event frames and stream comments', () => {
  const parsed = consumeEventStream({ line: '', event: {} }, ': heartbeat\n\n');

  expect(parsed.events).toEqual([]);
});

it('treats data fields without a separator as empty data', () => {
  const parsed = consumeEventStream({ line: '', event: {} }, 'data\n\n');

  expect(parsed.events).toEqual([{ data: '' }]);
});

it('preserves the named event type in event-stream messages', () => {
  const parsed = consumeEventStream({ line: '', event: {} }, 'event: progress\ndata: {}\n\n');

  expect(parsed.events).toEqual([{ event: 'progress', data: '{}' }]);
});

it('keeps an unspecified terminal state non-successful', () => {
  expect(presentationForJob({ ...job, state: 'failed' })).toEqual({ label: 'failed', successful: false });
});

it('describes explicit success as successful', () => {
  expect(presentationForJob({ ...job, state: 'succeeded' })).toEqual({ label: 'Transfer succeeded', successful: true });
});

it('labels table progress with readable row and byte counts', () => {
  expect(tableProgressLabel({ table: 'sales.Orders', rowsTransferred: 1_234, bytesTransferred: 5_678 })).toBe('sales.Orders: 1,234 rows, 5,678 bytes');
});
