import { HttpError, requestJson } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';

export type ConnectionSummary = Readonly<{ connectionId: string; displayName: string; providerId: string; health: string; eTag: string }>;
export type PlanReview = Readonly<{
  planId: string;
  version: number;
  canonicalHash: string;
  seal: Readonly<{ status: string; invalidationReasons: readonly Readonly<{ code: string; message: string }>[] }>;
  totals: Readonly<{ included: number; plannedWrites: number }>;
  selection: Readonly<{ selectionId: string; displayName: string }> | null;
  source: ConnectionSummary | null;
  target: ConnectionSummary | null;
}>;
export type PlanInput = Readonly<{ displayName: string; operatorNote: string | null; ifMatch: string; selectionId: string; sourceConnectionId: string; targetConnectionId: string }>;
export type PlanResponse = Readonly<{ planId: string; version: number; canonicalHash: string | null; eTag: string }>;
export type OperationReceipt = Readonly<{ operationId: string; state: string; statusUri: string; planId: string | null; jobId: string | null }>;
export type JobReceipt = OperationReceipt & Readonly<{ jobId: string }>;
export type OperationStatus = Readonly<{ operationId: string; operation: string; state: string; finished: boolean; failed: boolean; failureCode: string | null; jobId: string | null }>;
type Selections = Readonly<{ selections: readonly Readonly<{ selectionId: string; displayName: string }>[] }>;

export function getPlanReview(planId: string, authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<PlanReview>(`/api/plans/${planId}/review`, authentication, { signal });
}

export function getSelections(authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<Selections>('/api/selections', authentication, { signal });
}

export function getConnections(authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<readonly ConnectionSummary[]>('/api/connections', authentication, { signal });
}

export function savePlan(planId: string, body: PlanInput, authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<PlanResponse>(`/api/plans/${planId}`, authentication, { method: 'PUT', body, signal });
}

export function sealPlan(planId: string, authentication: AuthenticationAdapter) {
  return requestJson<OperationReceipt>(`/api/plans/${planId}/seal`, authentication, { method: 'POST' });
}

export function getOperationStatus(operationId: string, authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<OperationStatus>(`/api/operations/${operationId}`, authentication, { signal });
}

export function startPlan(planId: string, idempotencyKey: string, authentication: AuthenticationAdapter) {
  return requestJson<JobReceipt>(`/api/plans/${planId}/jobs`, authentication, { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey } });
}

export function requestErrorMessage(error: unknown, fallback: string, conflict: string) {
  if (!(error instanceof HttpError)) return fallback;
  const messages: Record<number, string> = { 401: 'Sign in to continue.', 403: 'You do not have permission to do that.', 404: 'The plan was not found.', 409: conflict };
  return messages[error.status] ?? (error.status >= 500 ? 'The service is unavailable. Try again.' : fallback);
}
