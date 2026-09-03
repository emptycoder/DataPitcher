import { describe, expect, it, vi } from 'vitest';
import type { JobEventResult, JobView } from './jobEvents';

const event = (eventName: string, payload: unknown) => ({ event: eventName, data: JSON.stringify(payload) });
const payload = (state: string, rowsTransferred = 4, bytesTransferred = 200) => ({ State: state, RowsTransferred: rowsTransferred, BytesTransferred: bytesTransferred });

const initialView = {
  state: 'queued' as const,
  rowsTransferred: 0,
  bytesTransferred: 0,
  totalRows: undefined,
  currentTable: undefined,
  failureDetail: undefined,
};

function stream(...chunks: string[]) {
  return new Response(new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(new TextEncoder().encode(chunk));
      controller.close();
    },
  }));
}

describe('job events', () => {
  it('surfaces malformed payloads without throwing', async () => {
    const { parseJobEvent } = await import('./jobEvents');

    expect(parseJobEvent(event('progress', { State: 'running', RowsTransferred: -1, BytesTransferred: 200 }))).toMatchObject({ type: 'problem', reason: 'malformed-payload' });
    expect(parseJobEvent({ event: 'progress', data: '{' })).toMatchObject({ type: 'problem', reason: 'malformed-payload' });
  });

  it('reduces an unmapped state to unknown', async () => {
    const { parseJobEvent, reduceJobEvent } = await import('./jobEvents');
    const parsed = parseJobEvent(event('state', payload('waiting-for-magic')));

    expect(parsed).toMatchObject({ type: 'event', state: 'unknown' });
    if (parsed.type === 'event') expect(reduceJobEvent(initialView, parsed)).toMatchObject({ state: 'unknown' });
    const illegal = parseJobEvent(event('state', payload('verifying')));
    if (illegal.type === 'event') expect(reduceJobEvent(initialView, illegal)).toMatchObject({ state: 'unknown' });
    const running = parseJobEvent(event('state', payload('running')));
    if (running.type === 'event') expect(reduceJobEvent({ ...initialView, state: 'unknown' }, running)).toMatchObject({ state: 'unknown' });
    expect(parseJobEvent(event('unmapped', payload('running')))).toEqual({ type: 'problem', reason: 'unknown-event' });
  });

  it('keeps verification failures failed', async () => {
    const { parseJobEvent, reduceJobEvent } = await import('./jobEvents');
    const parsed = parseJobEvent(event('state', payload('verificationfailed')));

    if (parsed.type === 'event') expect(reduceJobEvent({ ...initialView, state: 'verifying' }, parsed)).toMatchObject({ state: 'verificationfailed', failureDetail: undefined });
  });

  it('does not regress a terminal state', async () => {
    const { parseJobEvent, reduceJobEvent } = await import('./jobEvents');
    const parsed = parseJobEvent(event('progress', payload('running', 8, 400)));
    const failed = { ...initialView, state: 'failed' as const, rowsTransferred: 7, bytesTransferred: 300 };

    if (parsed.type === 'event') expect(reduceJobEvent(failed, parsed)).toEqual(failed);
  });

  it('retries one unauthorized stream response', async () => {
    const { streamJobEvents } = await import('./jobEvents');
    const request = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response(null, { status: 401 }));
    const authentication = { getAccessToken: vi.fn().mockResolvedValue('token') };
    const received = [];

    for await (const item of streamJobEvents('job-1', request, authentication)) received.push(item);

    expect(request).toHaveBeenCalledTimes(2);
    expect(authentication.getAccessToken).toHaveBeenCalledTimes(2);
    expect(received).toEqual([{ type: 'problem', reason: 'unauthorized' }]);
  });

  it('does not retry a forbidden stream response', async () => {
    const { streamJobEvents } = await import('./jobEvents');
    const request = vi.fn().mockResolvedValue(new Response(null, { status: 403 }));
    const authentication = { getAccessToken: vi.fn().mockResolvedValue('token') };
    const received = [];

    for await (const item of streamJobEvents('job-1', request, authentication)) received.push(item);

    expect(request).toHaveBeenCalledOnce();
    expect(received).toEqual([{ type: 'problem', reason: 'forbidden' }]);
  });

  it('surfaces an unavailable token and other failed responses', async () => {
    const { streamJobEvents } = await import('./jobEvents');
    const unauthenticated = [];
    for await (const item of streamJobEvents('job-1', vi.fn(), { getAccessToken: vi.fn().mockResolvedValue(null) })) unauthenticated.push(item);
    const unavailable = [];
    for await (const item of streamJobEvents('job-1', vi.fn().mockResolvedValue(new Response(null, { status: 500 })), { getAccessToken: vi.fn().mockResolvedValue('token') })) unavailable.push(item);

    expect(unauthenticated).toEqual([{ type: 'problem', reason: 'unauthorized' }]);
    expect(unavailable).toEqual([{ type: 'problem', reason: 'request-failed' }]);
  });

  it('surfaces a successful response without a stream body', async () => {
    const { streamJobEvents } = await import('./jobEvents');
    const received = [];

    for await (const item of streamJobEvents('job-1', vi.fn().mockResolvedValue(new Response(null)), { getAccessToken: vi.fn().mockResolvedValue('token') })) received.push(item);

    expect(received).toEqual([{ type: 'problem', reason: 'missing-body' }]);
  });

  it('reduces a streamed normal progression only to explicit success', async () => {
    const { reduceJobEvent, streamJobEvents } = await import('./jobEvents');
    const request = vi.fn().mockResolvedValue(stream(
      'id: 1\nevent: state\ndata: {"State":"preparing","RowsTransferred":1,"BytesTransferred":100}\n\nid: 2\nevent: state\ndata: {"State":"running","RowsTransferred":2,"BytesTransferred":200}\n\nid: 3\nevent: progress\ndata: {"State":"running","RowsTransferred":3,"BytesTransferred":300}\n\n',
      'id: 4\nevent: state\ndata: {"State":"verifying","RowsTransferred":3,"BytesTransferred":300}\n\nid: 5\nevent: state\ndata: {"State":"succeeded","RowsTransferred":4,"BytesTransferred":400}\n\n',
    ));
    const events: JobEventResult[] = [];
    for await (const item of streamJobEvents('job-1', request, { getAccessToken: vi.fn().mockResolvedValue('token') })) events.push(item);

    const view = events.reduce<JobView>((current, parsed) => parsed.type === 'event' ? reduceJobEvent(current, parsed) : current, { ...initialView, totalRows: 4, currentTable: 'orders' });

    expect(view).toMatchObject({ state: 'succeeded', rowsTransferred: 4, bytesTransferred: 400, totalRows: 4, currentTable: 'orders' });
  });
});
