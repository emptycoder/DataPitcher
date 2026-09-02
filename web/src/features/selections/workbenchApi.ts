import type { AuthenticationAdapter } from '../../auth/authAdapter';
import {
  getCompileSelectionUrl,
  getCountSelectionUrl,
  getGetSelectionWorkbenchSchemaUrl,
  getListSelectionsUrl,
  getPreviewSelectionUrl,
  getSaveSelectionUrl,
  type SelectionRequest,
} from '../../api/generated/client';
import {
  CompileSelectionResponse,
  CountSelectionResponse,
  GetSelectionWorkbenchSchemaResponse,
  ListSelectionsResponse,
  PreviewSelectionResponse,
  SaveSelectionResponse,
} from '../../api/generated/permissions.zod';
import { parseJson } from '../../api/parseJson';
import type { z } from 'zod';

export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export class SelectionRequestError extends Error {
  constructor(readonly status: 401 | 403 | 409 | 412) {
    super('Selection request failed.');
  }
}

async function requestSelection<T>(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal, url: string, init: RequestInit, schema: z.ZodType<T>): Promise<T> {
  const token = await authentication.getAccessToken();
  if (!token) throw new Error('Not authenticated.');
  const response = await request(url, { ...init, headers: { ...init.headers, Authorization: `Bearer ${token}` }, signal });
  if (response.status === 401 || response.status === 403 || response.status === 409 || response.status === 412) throw new SelectionRequestError(response.status);
  return parseJson(response, schema);
}

export function fetchSelectionWorkbenchSchema(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getGetSelectionWorkbenchSchemaUrl(), { method: 'GET' }, GetSelectionWorkbenchSchemaResponse);
}

export function fetchSavedSelections(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getListSelectionsUrl(), { method: 'GET' }, ListSelectionsResponse);
}

export function compileSelection(request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getCompileSelectionUrl(), jsonSelectionRequest(selection), CompileSelectionResponse);
}

export function fetchPreview(request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getPreviewSelectionUrl(), jsonSelectionRequest(selection), PreviewSelectionResponse);
}

export function countSelection(request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getCountSelectionUrl(), jsonSelectionRequest(selection), CountSelectionResponse);
}

export function saveSelection(request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest, signal: AbortSignal) {
  return requestSelection(request, authentication, signal, getSaveSelectionUrl(), jsonSelectionRequest(selection), SaveSelectionResponse);
}

function jsonSelectionRequest(selection: SelectionRequest): RequestInit {
  return { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(selection) };
}
