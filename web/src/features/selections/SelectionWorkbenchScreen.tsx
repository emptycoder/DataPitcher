import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { HttpError, requestJson } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { Button, DataTable, Field, InlineError, LoadingIndicator } from '../../ui';

export type SelectionWorkbenchScreenProps = Readonly<{ authentication: AuthenticationAdapter }>;

type Connection = Readonly<{ connectionId: string; displayName: string }>;
type SnapshotSummary = Readonly<{ snapshotId: string; hash: string; capturedAtUtc: string }>;
type SnapshotTable = Readonly<{ schema: string; name: string; columns: readonly { name: string }[]; primaryKey: { name: string; columns: readonly string[] } | null }>;
type Snapshot = Readonly<{ hash: string; tables: readonly SnapshotTable[] }>;
type SavedSelection = Readonly<{ selectionId: string; displayName: string; mode: string }>;
type SavedSelections = Readonly<{ selections: readonly SavedSelection[] }>;

const savedSelectionsKey = ['saved-selections'] as const;

function tableId(table: SnapshotTable) {
  return `${table.schema}.${table.name}`;
}

export function selectionErrorMessage(error: unknown, fallback = 'Unable to save selection.') {
  if (!(error instanceof HttpError)) return fallback;
  const messages: Readonly<Record<number, string>> = {
    400: 'Choose a snapshot root table and stable key before saving.',
    401: 'Sign in to save selections.',
    403: 'You do not have permission to save selections.',
    404: 'The selected connection or schema snapshot was not found.',
  };
  return messages[error.status] ?? (error.status >= 500 ? 'The selection service is temporarily unavailable.' : fallback);
}

export function SelectionWorkbenchScreen({ authentication }: SelectionWorkbenchScreenProps) {
  const queryClient = useQueryClient();
  const { hasPermission } = usePermissions();
  const [connectionId, setConnectionId] = useState('');
  const [snapshotId, setSnapshotId] = useState('');
  const [rootId, setRootId] = useState('');
  const [stableKeyColumns, setStableKeyColumns] = useState<readonly string[]>([]);
  const [rawSql, setRawSql] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const connections = useQuery({ queryKey: ['connections'], queryFn: ({ signal }) => requestJson<readonly Connection[]>('/api/connections', authentication, { signal }) });
  const snapshots = useQuery({ queryKey: ['snapshots', connectionId], queryFn: ({ signal }) => requestJson<readonly SnapshotSummary[]>(`/api/connections/${connectionId}/snapshots`, authentication, { signal }), enabled: Boolean(connectionId) });
  const snapshot = useQuery({ queryKey: ['snapshot', connectionId, snapshotId], queryFn: ({ signal }) => requestJson<Snapshot>(`/api/connections/${connectionId}/snapshots/${snapshotId}`, authentication, { signal }), enabled: Boolean(connectionId && snapshotId) });
  const savedSelections = useQuery({ queryKey: savedSelectionsKey, queryFn: ({ signal }) => requestJson<SavedSelections>('/api/selections', authentication, { signal }), retry: false });
  const save = useMutation({
    mutationFn: (body: unknown) => requestJson<SavedSelection>('/api/selections/save', authentication, { method: 'POST', body }),
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: savedSelectionsKey }),
  });
  const root = snapshot.data?.tables.find((table) => tableId(table) === rootId) ?? null;

  function selectConnection(value: string) {
    setConnectionId(value);
    setSnapshotId('');
    setRootId('');
    setStableKeyColumns([]);
  }

  function selectSnapshot(value: string) {
    setSnapshotId(value);
    setRootId('');
    setStableKeyColumns([]);
  }

  function selectRoot(value: string) {
    setRootId(value);
    setStableKeyColumns(snapshot.data?.tables.find((table) => tableId(table) === value)?.primaryKey?.columns ?? []);
  }

  function moveStableKey(index: number, direction: number) {
    setStableKeyColumns((columns) => {
      const next = [...columns];
      next.splice(index + direction, 0, next.splice(index, 1)[0]!);
      return next;
    });
  }

  function saveSelection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!rawSql.trim()) {
      setValidationError('Enter raw SQL before saving.');
      return;
    }
    setValidationError(null);
    save.mutate({
      mode: 'raw',
      visual: null,
      rawSql,
      parameters: [],
      schemaRevision: snapshot.data?.hash ?? '',
      connectionId: connectionId || null,
      snapshotId: snapshotId || null,
      rootSchema: root?.schema ?? null,
      rootTable: root?.name ?? null,
      stableKeyConstraintName: root?.primaryKey?.name ?? null,
      stableKeyColumns,
    });
  }

  return (
    <section aria-label="Selection workbench">
      <h2>Selection workbench</h2>
      <p>Use raw SQL to identify seed rows. We never infer a table or key from SQL: choose the source snapshot, root table, and stable key that identify each row for dependency closure.</p>
      <form aria-label="Save selection" onSubmit={saveSelection}>
        <Field label="Connection"><select value={connectionId} onChange={(event) => selectConnection(event.target.value)}><option value="">Choose connection</option>{connections.data?.map((connection) => <option key={connection.connectionId} value={connection.connectionId}>{connection.displayName}</option>)}</select></Field>
        <Field label="Snapshot"><select value={snapshotId} disabled={!connectionId} onChange={(event) => selectSnapshot(event.target.value)}><option value="">Choose snapshot</option>{snapshots.data?.map((snapshotSummary) => <option key={snapshotSummary.snapshotId} value={snapshotSummary.snapshotId}>{`${snapshotSummary.hash} (${snapshotSummary.capturedAtUtc})`}</option>)}</select></Field>
        <Field label="Root table"><select value={rootId} disabled={!snapshotId} onChange={(event) => selectRoot(event.target.value)}><option value="">Choose table with a primary key</option>{snapshot.data?.tables.filter((table) => table.primaryKey !== null).map((table) => <option key={tableId(table)} value={tableId(table)}>{tableId(table)}</option>)}</select></Field>
        {root?.primaryKey ? <section aria-label="Stable key"><p>Stable key constraint: {root.primaryKey.name}</p><ol aria-label="Stable key columns">{stableKeyColumns.map((column, index) => <li key={column}>{column} <Button disabled={index === 0} aria-label={`Move ${column} earlier`} onClick={() => moveStableKey(index, -1)}>Earlier</Button><Button disabled={index === stableKeyColumns.length - 1} aria-label={`Move ${column} later`} onClick={() => moveStableKey(index, 1)}>Later</Button></li>)}</ol></section> : <p>Choose a root table to state its stable key.</p>}
        <Field label="Raw SQL"><textarea value={rawSql} rows={8} onChange={(event) => setRawSql(event.target.value)} /></Field>
        <Button type="submit" disabled={save.isPending || !hasPermission('Selections.Write')}>Save selection</Button>
      </form>
      {validationError ? <InlineError>{validationError}</InlineError> : null}
      {save.isError ? <InlineError>{selectionErrorMessage(save.error)}</InlineError> : null}
      {save.isSuccess ? <p role="status">Selection saved.</p> : null}
      <section aria-label="Preview"><h3>Preview</h3><p>Preview is temporarily unavailable.</p></section>
      <section aria-label="Count seed rows"><h3>Count seed rows</h3><p>Counting seed rows is temporarily unavailable.</p></section>
      <section aria-label="Visual query builder"><h3>Visual query builder</h3><p>The visual query builder is temporarily unavailable. Use raw SQL.</p></section>
      <section aria-label="Saved selections">
        <h3>Saved selections</h3>
        {savedSelections.isPending ? <LoadingIndicator label="Loading saved selections." /> : null}
        {savedSelections.isError ? <InlineError>{selectionErrorMessage(savedSelections.error, 'Unable to load saved selections.')}</InlineError> : null}
        {savedSelections.data?.selections.length === 0 ? <p>No saved selections.</p> : null}
        {savedSelections.data && savedSelections.data.selections.length > 0 ? <DataTable><thead><tr><th scope="col">Selection</th><th scope="col">Mode</th></tr></thead><tbody>{savedSelections.data.selections.map((selection) => <tr key={selection.selectionId}><td>{selection.displayName}</td><td>{selection.mode}</td></tr>)}</tbody></DataTable> : null}
      </section>
    </section>
  );
}
