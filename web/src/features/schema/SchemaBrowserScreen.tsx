import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { HttpError, requestJson } from '../../api/http';
import { Link } from '../../app/router';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { Button, DataTable, Field, InlineError, LoadingIndicator, StatusBadge, TextInput } from '../../ui';

type Connection = Readonly<{ connectionId: string; displayName: string; providerId: string }>;
type Address = Readonly<{ schema: string; name: string }>;
type Table = Address & Readonly<{ columns: readonly Column[]; primaryKey: Key | null }>;
type Column = Readonly<{ name: string; storeType: string; isNullable: boolean }>;
type Key = Readonly<{ name: string; columns: readonly string[] }>;
type ForeignKey = Readonly<{ name: string; childTable: Address; parentTable: Address; childColumns: readonly string[]; parentColumns: readonly string[]; isEnforced: boolean; isTrusted: boolean }>;
type SnapshotSummary = Readonly<{ snapshotId: string; hash: string; capturedAtUtc: string }>;
type Snapshot = SnapshotSummary & Readonly<{ tables: readonly Table[]; foreignKeys: readonly ForeignKey[] }>;

export type SchemaBrowserScreenProps = Readonly<{ authentication: AuthenticationAdapter }>;

const tableName = (table: Address) => `${table.schema}.${table.name}`;
const tableKey = (table: Address) => `${table.schema}\0${table.name}`;

function errorMessage(error: unknown) {
  if (!(error instanceof HttpError)) return 'Unable to load the schema.';
  if (error.status === 401) return 'Sign in to browse the schema.';
  if (error.status === 403) return 'You do not have permission to browse this schema.';
  if (error.status === 404) return 'The requested connection or schema snapshot was not found.';
  if (error.status >= 500) return 'The schema service is unavailable. Try again.';
  return 'Unable to load the schema.';
}

export function SchemaBrowserScreen({ authentication }: SchemaBrowserScreenProps) {
  const { isVerified, hasPermission } = usePermissions();
  const [connectionId, setConnectionId] = useState('');
  const [snapshotId, setSnapshotId] = useState('');
  const [selectedTableKey, setSelectedTableKey] = useState('');
  const [filter, setFilter] = useState('');
  const connections = useQuery({ queryKey: ['schema-browser', 'connections'], queryFn: ({ signal }) => requestJson<readonly Connection[]>('/api/connections', authentication, { signal }) });
  const snapshots = useQuery({ queryKey: ['schema-browser', 'snapshots', connectionId], queryFn: ({ signal }) => requestJson<readonly SnapshotSummary[]>(`/api/connections/${connectionId}/snapshots`, authentication, { signal }), enabled: connectionId !== '' });
  const snapshot = useQuery({ queryKey: ['schema-browser', 'snapshot', connectionId, snapshotId], queryFn: ({ signal }) => requestJson<Snapshot>(`/api/connections/${connectionId}/snapshots/${snapshotId}`, authentication, { signal }), enabled: connectionId !== '' && snapshotId !== '' });

  if (isVerified && !hasPermission('Schema.Read')) return <section aria-label="Schema browser"><InlineError>You do not have permission to browse schemas.</InlineError></section>;
  if (connections.isPending) return <LoadingIndicator label="Loading connections." />;
  if (connections.isError) return <section aria-label="Schema browser"><InlineError>{errorMessage(connections.error)}</InlineError></section>;
  if (connections.data!.length === 0) return <section aria-label="Schema browser"><h2>Schema browser</h2><p>No connections are available.</p></section>;

  return (
    <section aria-label="Schema browser">
      <h2>Schema browser</h2>
      <Field label="Connection"><select value={connectionId} onChange={(event) => { setConnectionId(event.target.value); setSnapshotId(''); setSelectedTableKey(''); }}><option value="">Choose a connection</option>{connections.data!.map((connection) => <option key={connection.connectionId} value={connection.connectionId}>{connection.displayName}</option>)}</select></Field>
      {!connectionId ? <p>Choose a connection to browse its snapshots.</p> : <SnapshotPicker snapshots={snapshots} snapshotId={snapshotId} onSelect={(id) => { setSnapshotId(id); setSelectedTableKey(''); }} />}
      {snapshotId ? <SnapshotContents snapshot={snapshot} filter={filter} selectedTableKey={selectedTableKey} onFilter={setFilter} onSelectTable={setSelectedTableKey} /> : null}
    </section>
  );
}

type SnapshotPickerProps = Readonly<{ snapshots: ReturnType<typeof useQuery<readonly SnapshotSummary[]>>; snapshotId: string; onSelect: (snapshotId: string) => void }>;

function SnapshotPicker({ snapshots, snapshotId, onSelect }: SnapshotPickerProps) {
  if (snapshots.isPending) return <LoadingIndicator label="Loading snapshots." />;
  if (snapshots.isError) return <InlineError>{errorMessage(snapshots.error)}</InlineError>;
  if (snapshots.data!.length === 0) return <><p>No schema snapshots exist for this connection. Run a schema scan, then return here.</p><Link to="/connections">Run a schema scan from Connections.</Link></>;
  return <Field label="Snapshot"><select value={snapshotId} onChange={(event) => onSelect(event.target.value)}><option value="">Choose a snapshot</option>{snapshots.data!.map((item) => <option key={item.snapshotId} value={item.snapshotId}>{`${item.hash} — ${item.capturedAtUtc}`}</option>)}</select></Field>;
}

type SnapshotContentsProps = Readonly<{ snapshot: ReturnType<typeof useQuery<Snapshot>>; filter: string; selectedTableKey: string; onFilter: (filter: string) => void; onSelectTable: (key: string) => void }>;

function SnapshotContents({ snapshot, filter, selectedTableKey, onFilter, onSelectTable }: SnapshotContentsProps) {
  if (snapshot.isPending) return <LoadingIndicator label="Loading schema snapshot." />;
  if (snapshot.isError) return <InlineError>{errorMessage(snapshot.error)}</InlineError>;
  const data = snapshot.data!;
  const visibleTables = data.tables.filter((table) => tableName(table).toUpperCase().includes(filter.toUpperCase()));
  const selectedTable = data.tables.find((table) => tableKey(table) === selectedTableKey);

  return <div>
    <Field label="Filter tables"><TextInput type="search" value={filter} onChange={(event) => onFilter(event.target.value)} /></Field>
    {data.tables.length === 0 ? <p>This snapshot has no tables.</p> : null}
    {data.tables.length > 0 && visibleTables.length === 0 ? <p>{`No tables match "${filter}".`}</p> : null}
    <ul>{visibleTables.map((table) => <li key={tableKey(table)}><Button aria-pressed={tableKey(table) === selectedTableKey} onClick={() => onSelectTable(tableKey(table))}>{tableName(table)}</Button></li>)}</ul>
    {selectedTable ? <TableDetails table={selectedTable} foreignKeys={data.foreignKeys.filter((foreignKey) => tableKey(foreignKey.childTable) === selectedTableKey)} /> : <p>Choose a table to inspect its columns and foreign keys.</p>}
  </div>;
}

function TableDetails({ table, foreignKeys }: Readonly<{ table: Table; foreignKeys: readonly ForeignKey[] }>) {
  return <section aria-label={`${tableName(table)} details`}>
    <h3>{tableName(table)}</h3>
    <p>{table.primaryKey ? `${table.primaryKey.name} (${table.primaryKey.columns.join(', ')})` : 'No primary key.'}</p>
    <DataTable><caption>Columns</caption><thead><tr><th scope="col">Column</th><th scope="col">Type</th><th scope="col">Nullability</th></tr></thead><tbody>{table.columns.map((column) => <tr key={column.name}><td>{column.name}</td><td>{column.storeType}</td><td>{column.isNullable ? 'Nullable' : 'Not nullable'}</td></tr>)}</tbody></DataTable>
    <section aria-label="Foreign keys">
      <h4>{`Foreign keys (${foreignKeys.length})`}</h4>
      <p><strong>Unenforced or untrusted relationships can invalidate referential closure assumptions.</strong></p>
      {foreignKeys.length === 0 ? <p>No foreign keys from this table.</p> : <ul>{foreignKeys.map((foreignKey) => <li key={foreignKey.name}><strong>{foreignKey.name}</strong><p>{`${tableName(foreignKey.parentTable)} (${foreignKey.childColumns.join(', ')} → ${foreignKey.parentColumns.join(', ')})`}</p><StatusBadge state={foreignKey.isEnforced ? 'Enforced' : 'Not enforced'} /> <StatusBadge state={foreignKey.isTrusted ? 'Trusted' : 'Not trusted'} /></li>)}</ul>}
    </section>
  </section>;
}
