import { z } from 'zod';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { parseJson } from '../../api/parseJson';

export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export const ConnectionHealth = z.enum(['Unknown', 'Checking', 'Healthy', 'Degraded', 'Unhealthy']);
export type ConnectionHealth = z.infer<typeof ConnectionHealth>;

export const ConnectionResponse = z.object({
  connectionId: z.string(),
  displayName: z.string(),
  providerId: z.string(),
  health: ConnectionHealth,
  eTag: z.string(),
});
export type Connection = z.infer<typeof ConnectionResponse>;

export const ListConnectionsResponse = z.array(ConnectionResponse);

export const OperationReceiptResponse = z.object({
  operationId: z.string(),
  state: z.string(),
  statusUri: z.string(),
  connectionId: z.string().nullable().optional(),
  planId: z.string().nullable().optional(),
  jobId: z.string().nullable().optional(),
});

export type CreateConnectionRequest = Readonly<{ displayName: string; providerId: string; credentialId: string; ifMatch: string }>;

const connectionsUrl = '/api/connections';
const connectionChecksUrl = (connectionId: string) => `/api/connections/${connectionId}/checks`;

async function authorization(authentication: AuthenticationAdapter) {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  return { Authorization: `Bearer ${token}` };
}

export async function fetchConnections(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(connectionsUrl, { headers: await authorization(authentication), signal }), ListConnectionsResponse);
}

export async function createConnection(body: CreateConnectionRequest, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(connectionsUrl, { method: 'POST', headers: { ...await authorization(authentication), 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal }), ConnectionResponse);
}

export async function queueConnectionCheck(connectionId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return parseJson(await request(connectionChecksUrl(connectionId), { method: 'POST', headers: await authorization(authentication), signal }), OperationReceiptResponse);
}
