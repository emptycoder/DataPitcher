import { useQuery } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import type { SelectionRequest } from '../../api/generated/client';
import type { RequestFunction } from './workbenchApi';
import { countQueryOptions, previewQueryKey, previewQueryOptions, type SelectionQueryDraft } from './workbenchQueries';
import { toRequestState, type RequestState } from './requestState';
import type { VirtualizerAdapter } from './virtualGrid';

type PreviewTabProps = {
  virtualizer: VirtualizerAdapter;
  queryClient: { cancelQueries: (filters: { queryKey: readonly unknown[] }) => Promise<void> };
  draft: SelectionQueryDraft;
  request: RequestFunction;
  authentication: AuthenticationAdapter;
  selection: SelectionRequest;
};

type PreviewStatePanelProps = {
  state: RequestState;
  onRetry: () => void;
  onRefresh: () => void;
};

export function displayPreviewValue(value: unknown): string {
  return value === null ? 'NULL' : String(value);
}

export function PreviewStatePanel({ state, onRetry, onRefresh }: PreviewStatePanelProps) {
  switch (state) {
    case 'loading': return <p aria-live="polite" aria-busy="true">Loading preview</p>;
    case 'empty': return <p aria-live="polite">No rows match this selection</p>;
    case 'error': return <button type="button" onClick={onRetry}>Retry preview</button>;
    case 'forbidden': return <p aria-live="polite">You do not have access to preview this selection.</p>;
    case 'cancelled': return <p aria-live="polite">Preview cancelled</p>;
    case 'stale': return <button type="button" onClick={onRefresh}>Refresh preview</button>;
    case 'tokenExpired': return <p aria-live="polite">Sign in to preview this selection.</p>;
    case 'ready': return null;
  }
}

export function PreviewTab({ virtualizer, queryClient, draft, request, authentication, selection }: PreviewTabProps) {
  const scrollElement = useRef<HTMLDivElement>(null);
  const [countRequested, setCountRequested] = useState(false);
  const preview = useQuery(previewQueryOptions(draft, request, authentication, selection));
  const rows = preview.data?.rows ?? [];
  const state = toRequestState({ pending: preview.isPending && preview.fetchStatus !== 'idle', empty: rows.length === 0, cancelled: preview.isPending && preview.fetchStatus === 'idle', error: preview.error });
  const grid = virtualizer.useGrid(rows.length, scrollElement);

  return (
    <section aria-label="Preview">
      <button type="button" onClick={() => queryClient.cancelQueries({ queryKey: previewQueryKey(draft) })}>Cancel preview</button>
      <PreviewStatePanel state={state} onRetry={preview.refetch} onRefresh={preview.refetch} />
      {state === 'ready' ? (
        <div ref={scrollElement} style={{ maxHeight: '24rem', overflow: 'auto' }}>
          <table>
            <thead><tr>{preview.data!.columns.map((column) => <th key={column} scope="col" style={{ position: 'sticky', top: 0 }}>{column}</th>)}</tr></thead>
            <tbody style={{ height: grid.totalHeight, position: 'relative' }}>
              {grid.items.map((item) => {
                const row = rows[item.index]!;
                return <tr key={item.index} style={{ transform: `translateY(${item.start}px)` }}>{preview.data!.columns.map((column) => <td key={column}>{displayPreviewValue(row[column])}</td>)}</tr>;
              })}
            </tbody>
          </table>
        </div>
      ) : null}
      <button type="button" onClick={() => setCountRequested(true)}>Count distinct stable keys</button>
      {countRequested ? <CountResult draft={draft} request={request} authentication={authentication} selection={selection} /> : null}
    </section>
  );
}

function CountResult({ draft, request, authentication, selection }: Omit<PreviewTabProps, 'virtualizer' | 'queryClient'>) {
  const count = useQuery(countQueryOptions(draft, request, authentication, selection));
  const state = toRequestState({ pending: count.isPending && count.fetchStatus !== 'idle', empty: false, cancelled: count.isPending && count.fetchStatus === 'idle', error: count.error });
  return state === 'ready'
    ? <><p aria-live="polite">Distinct stable keys: {count.data!.distinctStableKeyCount}</p><p>Joined rows are not counted separately.</p></>
    : <output aria-live="polite">Count {state}</output>;
}
