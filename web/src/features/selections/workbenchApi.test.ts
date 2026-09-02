import { QueryClient } from '@tanstack/react-query';
import { expect, it, vi } from 'vitest';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import {
  getCompileSelectionUrl,
  getCountSelectionUrl,
  getGetSelectionWorkbenchSchemaUrl,
  getListSelectionsUrl,
  getPreviewSelectionUrl,
  getSaveSelectionUrl,
} from '../../api/generated/client';
import type { SelectionRequest } from '../../api/generated/client';
import {
  compileSelection,
  countSelection,
  fetchSelectionWorkbenchSchema,
  fetchPreview,
  fetchSavedSelections,
  saveSelection,
  SelectionRequestError,
} from './workbenchApi';
import { countQueryOptions, previewQueryKey, previewQueryOptions } from './workbenchQueries';
import { toRequestState } from './requestState';

const principal = { subjectId: 'operator-1', tenantId: 'tenant-1' };
const visual = { root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id'] }, joins: [], predicate: null } as const;
const selectionRequest: SelectionRequest = { mode: 'visual', visual, rawSql: null, parameters: [], schemaRevision: 'schema-1' };
const draft = { connectionId: 'connection-1', selectionId: 'selection-1', sqlSnapshot: 'snapshot-1', visual, schemaRevision: 'schema-1' };

function responseFor(input: RequestInfo | URL): Response {
  switch (String(input)) {
    case getGetSelectionWorkbenchSchemaUrl():
      return new Response(JSON.stringify({ tables: [{ tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id'], columns: [{ name: 'id', valueKind: 'int' }] }], foreignKeys: [], schemaRevision: 'schema-1' }));
    case getListSelectionsUrl():
      return new Response(JSON.stringify({ selections: [{ selectionId: 'selection-1', displayName: 'Orders', version: 1, eTag: 'etag-1', mode: 'visual', warnings: [] }] }));
    case getCompileSelectionUrl():
      return new Response(JSON.stringify({ sqlSnapshot: 'snapshot-1', parameters: [], warnings: [], schemaRevision: 'schema-1' }));
    case getPreviewSelectionUrl():
      return new Response(JSON.stringify({ columns: ['id'], rows: [{ id: 1 }], hasMore: false, revision: 'schema-1' }));
    case getCountSelectionUrl():
      return new Response(JSON.stringify({ distinctStableKeyCount: 1 }));
    case getSaveSelectionUrl():
      return new Response(JSON.stringify({ selectionId: 'selection-1', displayName: 'Orders', version: 1, eTag: 'etag-1', mode: 'visual', warnings: [] }));
    default:
      throw new Error(`Unexpected URL: ${String(input)}`);
  }
}

it('classifies every workbench request state', () => {
  expect([
    toRequestState({ pending: true, empty: false, cancelled: false, error: null }),
    toRequestState({ pending: false, empty: true, cancelled: false, error: null }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: new Error() }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: new SelectionRequestError(403) }),
    toRequestState({ pending: false, empty: false, cancelled: true, error: null }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: new SelectionRequestError(409) }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: new SelectionRequestError(412) }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: new SelectionRequestError(401) }),
    toRequestState({ pending: false, empty: false, cancelled: false, error: null }),
  ]).toEqual(['loading', 'empty', 'error', 'forbidden', 'cancelled', 'stale', 'stale', 'tokenExpired', 'ready']);
});

it('uses generated URLs, authorization, and generated schemas for every selection request', async () => {
  const request = vi.fn(async (input: RequestInfo | URL) => responseFor(input));
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'token-1');
  const signal = new AbortController().signal;

  await expect(fetchSelectionWorkbenchSchema(request, authentication, signal)).resolves.toMatchObject({ schemaRevision: 'schema-1' });
  await expect(fetchSavedSelections(request, authentication, signal)).resolves.toMatchObject({ selections: [{ selectionId: 'selection-1' }] });
  await expect(compileSelection(request, authentication, selectionRequest, signal)).resolves.toMatchObject({ sqlSnapshot: 'snapshot-1' });
  await expect(fetchPreview(request, authentication, selectionRequest, signal)).resolves.toMatchObject({ rows: [{ id: 1 }] });
  await expect(countSelection(request, authentication, selectionRequest, signal)).resolves.toMatchObject({ distinctStableKeyCount: 1 });
  await expect(saveSelection(request, authentication, selectionRequest, signal)).resolves.toMatchObject({ selectionId: 'selection-1' });
  expect(request).toHaveBeenCalledTimes(6);
  for (const url of [getGetSelectionWorkbenchSchemaUrl(), getListSelectionsUrl(), getCompileSelectionUrl(), getPreviewSelectionUrl(), getCountSelectionUrl(), getSaveSelectionUrl()]) {
    expect(request).toHaveBeenCalledWith(url, expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer token-1' }), signal }));
  }
});

it('rejects malformed data, mapped statuses, and missing authentication', async () => {
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'token-1');
  await expect(fetchSelectionWorkbenchSchema(vi.fn(async () => new Response(JSON.stringify({ tables: [] }))), authentication, new AbortController().signal)).rejects.toThrow();
  for (const status of [401, 403, 409, 412]) {
    await expect(fetchPreview(vi.fn(async () => new Response(null, { status })), authentication, selectionRequest, new AbortController().signal)).rejects.toMatchObject({ status });
  }
  await authentication.signOut();
  await expect(fetchSelectionWorkbenchSchema(vi.fn(), authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
});

it('keys preview data semantically and cancels the request signal through Query', async () => {
  const authentication = createDevelopmentAuthenticationAdapter(principal, 'token-1');
  expect(previewQueryKey(draft)).toEqual(['selectionPreview', 'connection-1', 'snapshot-1', 'selection-1', JSON.stringify(visual), 'schema-1']);

  let aborted = false;
  let started = () => {};
  const startedRequest = new Promise<void>((resolve) => { started = resolve; });
  const request = vi.fn((_input: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
    init?.signal?.addEventListener('abort', () => {
      aborted = init.signal?.aborted ?? false;
      reject(new DOMException('Aborted', 'AbortError'));
    });
    started();
  }));
  const client = new QueryClient();
  const preview = client.fetchQuery(previewQueryOptions(draft, request, authentication, selectionRequest));
  await startedRequest;
  await client.cancelQueries({ queryKey: previewQueryKey(draft) });
  await expect(preview).rejects.toBeDefined();
  expect(aborted).toBe(true);

  await expect(new QueryClient().fetchQuery(countQueryOptions(draft, vi.fn(async () => responseFor(getCountSelectionUrl())), authentication, selectionRequest))).resolves.toMatchObject({ distinctStableKeyCount: 1 });
});
