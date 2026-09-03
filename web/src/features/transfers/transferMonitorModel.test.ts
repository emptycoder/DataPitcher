import { expect, it } from 'vitest';
import {
  consumeEventStream,
  presentationForJob,
  reduceJobEvent,
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
