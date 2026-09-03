import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { requestJson } from '../../api/http';

type Message = Readonly<{ code: string; message: string }>;
type Counts = Readonly<{ included?: number | null; plannedWrites?: number | null; inserts?: number | null; updates?: number | null; estimatedBytes?: number | null }>;
type Address = Readonly<{ schema: string; name: string }>;

export type PlanReview = Readonly<{
  planId: string;
  version: number;
  canonicalHash: string | null;
  seal: Readonly<{ status: string; invalidationReasons: readonly Message[] }>;
  totals: Counts;
  tables: readonly (Counts & Readonly<{ source: Address; target: Address; state: string; transferOrder: number }>)[];
}>;

export type InclusionPath = Readonly<{
  table: string;
  stableKey: string;
  rootSelection: string;
  steps: readonly Readonly<{ relationship: string; from: string; to: string; reason: string }>[];
}>;

export function getPlanReview(planId: string, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestJson<PlanReview>(`/api/plans/${planId}/review`, authentication, { signal });
}

export function getInclusionPath(planId: string, table: string, stableKey: string, authentication: AuthenticationAdapter, signal?: AbortSignal) {
  return requestJson<InclusionPath>(`/api/plans/${planId}/inclusion-paths`, authentication, { method: 'POST', body: { table, stableKey }, signal });
}
