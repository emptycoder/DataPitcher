import { SelectionRequestError } from './workbenchApi';

export type RequestState = 'loading' | 'ready' | 'empty' | 'error' | 'forbidden' | 'cancelled' | 'stale' | 'tokenExpired';

export function toRequestState(input: { pending: boolean; empty: boolean; cancelled: boolean; error: unknown }): RequestState {
  if (input.pending) return 'loading';
  if (input.cancelled) return 'cancelled';
  if (input.error instanceof SelectionRequestError && input.error.status === 401) return 'tokenExpired';
  if (input.error instanceof SelectionRequestError && input.error.status === 403) return 'forbidden';
  if (input.error instanceof SelectionRequestError && (input.error.status === 409 || input.error.status === 412)) return 'stale';
  if (input.error) return 'error';
  return input.empty ? 'empty' : 'ready';
}
