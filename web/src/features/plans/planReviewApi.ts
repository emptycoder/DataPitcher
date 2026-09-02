import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { getPlanInclusionPathUrl, getPlanReviewUrl, getStartPlanJobUrl } from '../../api/generated/client';
import { PlanInclusionPathResponse, PlanReviewResponse, StartPlanJobResponse } from '../../api/generated/permissions.zod';
import { parseJson } from '../../api/parseJson';

export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
export type InclusionRequest = Readonly<{ table: string; stableKey: string }>;

async function authorization(authentication: AuthenticationAdapter) {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  return { Authorization: `Bearer ${token}` };
}

export async function fetchPlanReview(planId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(getPlanReviewUrl(planId), { headers: await authorization(authentication), signal }), PlanReviewResponse);
}

export async function fetchInclusionPath(planId: string, body: InclusionRequest, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(getPlanInclusionPathUrl(planId), { method: 'POST', headers: { ...await authorization(authentication), 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal }), PlanInclusionPathResponse);
}

export async function startPlanJob(planId: string, idempotencyKey: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(getStartPlanJobUrl(planId), { method: 'POST', headers: { ...await authorization(authentication), 'Idempotency-Key': idempotencyKey }, signal }), StartPlanJobResponse);
}
