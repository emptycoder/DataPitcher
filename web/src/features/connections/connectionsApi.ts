import { z } from 'zod';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { HttpError, requestJson } from '../../api/http';

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
type ConnectionRequestOptions = Readonly<{ method?: string; body?: unknown }>;

const connectionsUrl = '/api/connections';
const connectionChecksUrl = (connectionId: string) => `/api/connections/${connectionId}/checks`;
const schemaScansUrl = (connectionId: string) => `/api/connections/${connectionId}/schema-scans`;

async function authorization(authentication: AuthenticationAdapter) {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  return { Authorization: `Bearer ${token}` };
}

async function requestConnection<T>(url: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal, schema: z.ZodType<T>, options: ConnectionRequestOptions = {}) {
  if (request === fetch) return schema.parse(await requestJson<unknown>(url, authentication, { ...options, signal }));
  const response = await request(url, { method: options.method, headers: { ...await authorization(authentication), ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: options.body === undefined ? undefined : JSON.stringify(options.body), signal });
  if (!response.ok) {
    let problem: unknown = null;
    try {
      problem = await response.json();
    } catch {
      problem = null;
    }
    throw new HttpError(response.status, problem);
  }
  return schema.parse(await response.json());
}

export async function fetchConnections(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestConnection(connectionsUrl, request, authentication, signal, ListConnectionsResponse);
}

export async function createConnection(body: CreateConnectionRequest, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestConnection(connectionsUrl, request, authentication, signal, ConnectionResponse, { method: 'POST', body });
}

export async function queueConnectionCheck(connectionId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestConnection(connectionChecksUrl(connectionId), request, authentication, signal, OperationReceiptResponse, { method: 'POST' });
}

export async function queueSchemaScan(connectionId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestConnection(schemaScansUrl(connectionId), request, authentication, signal, OperationReceiptResponse, { method: 'POST' });
}
