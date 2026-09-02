import type { AuthenticationAdapter } from '../auth/authAdapter';
import { getEffectivePermissionsUrl } from './generated/client';
import { EffectivePermissionsResponse } from './generated/permissions.zod';
import { parseJson } from './parseJson';

export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export async function fetchEffectivePermissions(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  const response = await request(getEffectivePermissionsUrl(), { headers: { Authorization: `Bearer ${token}` }, signal });
  return parseJson(response, EffectivePermissionsResponse);
}
