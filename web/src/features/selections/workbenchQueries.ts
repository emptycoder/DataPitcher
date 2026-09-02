import type { AuthenticationAdapter } from '../../auth/authAdapter';
import type { SelectionRequest } from '../../api/generated/client';
import { selectionFingerprint, type VisualSelection } from './selectionAst';
import { countSelection, fetchPreview, type RequestFunction } from './workbenchApi';

export type SelectionQueryDraft = {
  connectionId: string;
  selectionId: string | null;
  sqlSnapshot: string | null;
  visual: VisualSelection;
  schemaRevision: string;
};

export function previewQueryKey(draft: SelectionQueryDraft) {
  return ['selectionPreview', draft.connectionId, draft.sqlSnapshot, draft.selectionId, selectionFingerprint(draft.visual), draft.schemaRevision] as const;
}

export function countQueryKey(draft: SelectionQueryDraft) {
  return ['selectionCount', draft.connectionId, draft.sqlSnapshot, draft.selectionId, selectionFingerprint(draft.visual), draft.schemaRevision] as const;
}

export function previewQueryOptions(draft: SelectionQueryDraft, request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest) {
  return { queryKey: previewQueryKey(draft), retry: false, queryFn: ({ signal }: { signal: AbortSignal }) => fetchPreview(request, authentication, selection, signal) };
}

export function countQueryOptions(draft: SelectionQueryDraft, request: RequestFunction, authentication: AuthenticationAdapter, selection: SelectionRequest) {
  return { queryKey: countQueryKey(draft), retry: false, queryFn: ({ signal }: { signal: AbortSignal }) => countSelection(request, authentication, selection, signal) };
}
