import type { AuthenticationAdapter } from '../auth/authAdapter';
import { getPlanSchemaDependencyGraphUrl } from './generated/client';
import { PlanSchemaDependencyGraphResponse } from './generated/permissions.zod';
import { parseJson } from './parseJson';
import type { RequestFunction } from './effectivePermissionsApi';

export async function fetchPlanDependencyGraph(planId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  return parseJson(await request(getPlanSchemaDependencyGraphUrl(planId), { headers: { Authorization: `Bearer ${token}` }, signal }), PlanSchemaDependencyGraphResponse);
}
