import { z } from 'zod';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { HttpError, requestJson } from './http';

export const OperationStatusSchema = z.object({
  operationId: z.string(),
  operation: z.string(),
  state: z.string(),
  finished: z.boolean(),
  failed: z.boolean(),
  failureCode: z.string().nullable(),
  failureDetail: z.string().nullable().optional(),
  connectionId: z.string().nullable().optional(),
  snapshotId: z.string().nullable().optional(),
  planId: z.string().nullable().optional(),
  jobId: z.string().nullable().optional(),
});
export type OperationStatus = z.infer<typeof OperationStatusSchema>;

export const operationsApi = {
  get: (operationId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/operations/${operationId}`, auth, { signal }).then((data) => OperationStatusSchema.parse(data)),
};

export type PollOutcome = Readonly<{ kind: 'finished'; status: OperationStatus } | { kind: 'missing' } | { kind: 'timeout'; last: OperationStatus | null }>;

/** Polls an operation until it finishes. Reports every intermediate status so callers can render progress. */
export async function pollOperation(
  operationId: string,
  auth: AuthenticationAdapter,
  options: Readonly<{ signal?: AbortSignal; intervalMs?: number; timeoutMs?: number; onStatus?: (status: OperationStatus) => void }> = {},
): Promise<PollOutcome> {
  const interval = options.intervalMs ?? 750;
  const deadline = Date.now() + (options.timeoutMs ?? 5 * 60_000);
  let last: OperationStatus | null = null;
  while (Date.now() < deadline) {
    if (options.signal?.aborted) return { kind: 'timeout', last };
    try {
      const status = await operationsApi.get(operationId, auth, options.signal);
      last = status;
      options.onStatus?.(status);
      if (status.finished) return { kind: 'finished', status };
    } catch (error) {
      if (error instanceof HttpError && error.status === 404) return { kind: 'missing' };
      if (options.signal?.aborted) return { kind: 'timeout', last };
      throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, interval));
  }
  return { kind: 'timeout', last };
}
