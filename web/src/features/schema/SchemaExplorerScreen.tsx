import { useDeferredValue, useEffect, useMemo, useState } from 'react';
import type { Snapshot, SnapshotForeignKey, SnapshotTable } from '../../api/connections';
import { formatDateTime, formatNumber } from '../../api/format';
import { describeError } from '../../api/problem';
import { usePermissions } from '../../auth/permissions';
import { Link, navigate } from '../../app/router';
import { useSourceConnectionId } from '../../stores/sessionStore';
import { Alert, Badge, Button, Card, Code, DataTable, EmptyState, Field, PageHeader, Select, Skeleton, Stat, Tabs, TextInput, cx } from '../../ui';
import { Icons } from '../../ui/icons';
import type { SchemaGraphProjection, SchemaTableAddress } from '../graph/graphLayout';
import { useConnections, useSnapshot, useSnapshots } from '../shared/queries';
import { SchemaGraph, tableKey } from './SchemaGraph';

export function SchemaExplorerScreen({ connectionId, snapshotId }: Readonly<{ connectionId: string | null; snapshotId: string | null }>) {
  const { isVerified, hasPermission } = usePermissions();
  const connections = useConnections();
  const sourceId = useSourceConnectionId();
  const effectiveConnectionId = connectionId ?? sourceId ?? connections.data?.[0]?.connectionId ?? null;
  const snapshots = useSnapshots(effectiveConnectionId);
  const effectiveSnapshotId = snapshotId ?? snapshots.data?.[0]?.snapshotId ?? null;
  const snapshot = useSnapshot(effectiveConnectionId, effectiveSnapshotId);

  // Keep the URL canonical once defaults resolve so links and refreshes are stable.
  useEffect(() => {
    if (!effectiveConnectionId || !effectiveSnapshotId) return;
    if (connectionId !== effectiveConnectionId || snapshotId !== effectiveSnapshotId) navigate(`/schema/${effectiveConnectionId}/${effectiveSnapshotId}`, { replace: true });
  }, [connectionId, snapshotId, effectiveConnectionId, effectiveSnapshotId]);

  if (isVerified && !hasPermission('Schema.Read')) {
    return <Alert tone="danger">You do not have permission to browse schemas.</Alert>;
  }

  const connection = connections.data?.find((item) => item.connectionId === effectiveConnectionId) ?? null;

  return (
    <>
      <PageHeader
        actions={
          <div className="flex flex-wrap items-end gap-3">
            <Field label="Connection">
              <Select
                className="min-w-56"
                onChange={(event) => navigate(event.target.value ? `/schema/${event.target.value}` : '/schema')}
                value={effectiveConnectionId ?? ''}
              >
                <option value="">Choose…</option>
                {(connections.data ?? []).map((item) => (
                  <option key={item.connectionId} value={item.connectionId}>
                    {item.displayName}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Snapshot">
              <Select
                className="min-w-56"
                disabled={!effectiveConnectionId || (snapshots.data?.length ?? 0) === 0}
                onChange={(event) => navigate(`/schema/${effectiveConnectionId}/${event.target.value}`)}
                value={effectiveSnapshotId ?? ''}
              >
                {(snapshots.data ?? []).map((item, index) => (
                  <option key={item.snapshotId} value={item.snapshotId}>
                    {index === 0 ? 'Latest · ' : ''}
                    {formatDateTime(item.capturedAtUtc)} · {item.hash.slice(0, 8)}
                  </option>
                ))}
                {(snapshots.data?.length ?? 0) === 0 ? <option value="">No snapshots</option> : null}
              </Select>
            </Field>
          </div>
        }
        description="Browse captured tables, keys and foreign keys, and see how tables depend on each other."
        title="Schema explorer"
      />

      {connections.isPending || (effectiveConnectionId && snapshots.isPending) ? (
        <Skeleton className="h-[560px]" />
      ) : !effectiveConnectionId ? (
        <Card padded={false}>
          <EmptyState
            action={<Button onClick={() => navigate('/connections')} variant="primary">Go to connections</Button>}
            description="Register a connection and scan its schema first."
            icon={<Icons.Schema size={22} />}
            title="No connection selected"
          />
        </Card>
      ) : snapshots.isError ? (
        <Alert tone="danger">{describeError(snapshots.error)}</Alert>
      ) : !effectiveSnapshotId ? (
        <Card padded={false}>
          <EmptyState
            action={<Button onClick={() => navigate('/connections')} variant="primary">Scan schema</Button>}
            description={`${connection?.displayName ?? 'This connection'} has no schema snapshot yet. Run a scan from the Connections page.`}
            icon={<Icons.Schema size={22} />}
            title="No snapshot captured"
          />
        </Card>
      ) : snapshot.isPending ? (
        <Skeleton className="h-[560px]" />
      ) : snapshot.isError ? (
        <Alert tone="danger">{describeError(snapshot.error)}</Alert>
      ) : (
        <SnapshotExplorer connectionId={effectiveConnectionId} snapshot={snapshot.data} />
      )}
    </>
  );
}

function SnapshotExplorer({ connectionId, snapshot }: Readonly<{ connectionId: string; snapshot: Snapshot }>) {
  const [filter, setFilter] = useState('');
  const deferredFilter = useDeferredValue(filter);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [tab, setTab] = useState<'graph' | 'table'>('graph');

  const graph = useMemo<SchemaGraphProjection>(
    () => ({
      tables: snapshot.tables.map((table) => ({ schema: table.schema, name: table.name })),
      edges: snapshot.foreignKeys.map((fk) => ({ child: fk.childTable, parent: fk.parentTable, foreignKeyName: fk.name })),
    }),
    [snapshot],
  );

  const tablesBySchema = useMemo(() => {
    const groups = new Map<string, SnapshotTable[]>();
    const needle = deferredFilter.trim().toLowerCase();
    for (const table of snapshot.tables) {
      if (needle && !`${table.schema}.${table.name}`.toLowerCase().includes(needle)) continue;
      const group = groups.get(table.schema) ?? [];
      group.push(table);
      groups.set(table.schema, group);
    }
    return [...groups.entries()].toSorted(([a], [b]) => a.localeCompare(b));
  }, [snapshot, deferredFilter]);

  const selectedTable = snapshot.tables.find((table) => tableKey(table) === selectedKey) ?? null;
  const outbound = selectedTable ? snapshot.foreignKeys.filter((fk) => tableKey(fk.childTable) === selectedKey) : [];
  const inbound = selectedTable ? snapshot.foreignKeys.filter((fk) => tableKey(fk.parentTable) === selectedKey) : [];
  const unenforced = snapshot.foreignKeys.filter((fk) => !fk.isEnforced || !fk.isTrusted).length;
  const fkCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const fk of snapshot.foreignKeys) counts.set(tableKey(fk.childTable), (counts.get(tableKey(fk.childTable)) ?? 0) + 1);
    return counts;
  }, [snapshot]);

  function select(table: SchemaTableAddress) {
    setSelectedKey(tableKey(table));
    setTab('table');
  }

  return (
    <div className="grid gap-5">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Stat icon={<Icons.Table size={16} />} label="Tables" value={formatNumber(snapshot.tables.length)} />
        <Stat icon={<Icons.Link size={16} />} label="Foreign keys" tone="info" value={formatNumber(snapshot.foreignKeys.length)} />
        <Stat
          hint={unenforced > 0 ? 'Cannot be trusted for closure pruning' : 'All enforced and trusted'}
          icon={<Icons.Alert size={16} />}
          label="Unenforced / untrusted"
          tone={unenforced > 0 ? 'warning' : 'success'}
          value={formatNumber(unenforced)}
        />
        <Stat hint={formatDateTime(snapshot.capturedAtUtc)} icon={<Icons.Clock size={16} />} label="Snapshot" value={<span className="font-mono text-base">{snapshot.hash.slice(0, 12)}</span>} />
      </div>

      <div className="grid gap-5 lg:grid-cols-[300px_1fr]">
        <Card className="flex max-h-[720px] flex-col" padded={false}>
          <div className="border-b border-border p-3">
            <div className="relative">
              <Icons.Search className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-fg-faint" size={15} />
              <TextInput className="pl-9" onChange={(event) => setFilter(event.target.value)} placeholder="Filter tables…" type="search" value={filter} />
            </div>
          </div>
          <div className="scrollbar-thin flex-1 overflow-y-auto p-2">
            {tablesBySchema.length === 0 ? <p className="p-3 text-sm text-fg-muted">No tables match.</p> : null}
            {tablesBySchema.map(([schema, tables]) => (
              <div className="mb-2" key={schema}>
                <div className="px-2 py-1 font-mono text-[11px] font-semibold tracking-wide text-fg-faint uppercase">{schema}</div>
                {tables.map((table) => {
                  const key = tableKey(table);
                  const active = key === selectedKey;
                  return (
                    <button
                      aria-pressed={active}
                      className={cx(
                        'flex w-full items-center justify-between gap-2 rounded-lg px-2.5 py-1.5 text-left text-[13px] transition-colors',
                        active ? 'bg-accent-soft font-semibold text-accent' : 'text-fg hover:bg-surface-2',
                      )}
                      key={key}
                      onClick={() => select(table)}
                      type="button"
                    >
                      <span className="truncate">{table.name}</span>
                      <span className="flex items-center gap-1.5 text-[11px] text-fg-faint">
                        {table.primaryKey ? <Icons.Key size={12} /> : null}
                        {fkCounts.get(key) ? <span>{fkCounts.get(key)} fk</span> : null}
                      </span>
                    </button>
                  );
                })}
              </div>
            ))}
          </div>
        </Card>

        <div className="grid gap-4">
          <Tabs
            items={[
              { value: 'graph', label: 'Dependency graph' },
              { value: 'table', label: selectedTable ? selectedTable.name : 'Table details' },
            ]}
            onChange={setTab}
            value={tab}
          />
          {tab === 'graph' ? (
            <SchemaGraph graph={graph} height={640} onSelect={select} selectedKey={selectedKey} />
          ) : selectedTable ? (
            <TableDetails connectionId={connectionId} inbound={inbound} outbound={outbound} snapshot={snapshot} table={selectedTable} onSelectTable={(address) => setSelectedKey(tableKey(address))} />
          ) : (
            <Card padded={false}>
              <EmptyState description="Pick a table from the list or the graph to inspect its columns and relationships." icon={<Icons.Table size={22} />} title="No table selected" />
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}

function TableDetails({
  connectionId,
  snapshot,
  table,
  outbound,
  inbound,
  onSelectTable,
}: Readonly<{
  connectionId: string;
  snapshot: Snapshot;
  table: SnapshotTable;
  outbound: readonly SnapshotForeignKey[];
  inbound: readonly SnapshotForeignKey[];
  onSelectTable: (address: SchemaTableAddress) => void;
}>) {
  const { hasPermission } = usePermissions();
  const key = tableKey(table);
  const pkColumns = new Set(table.primaryKey?.columns ?? []);
  const fkColumns = new Set(outbound.flatMap((fk) => fk.childColumns));

  return (
    <div className="grid gap-4">
      <Card>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <div className="font-mono text-xs text-fg-faint">{table.schema}</div>
            <h2 className="text-xl font-bold text-fg">{table.name}</h2>
            <div className="mt-2 flex flex-wrap gap-2">
              {table.primaryKey ? (
                <Badge tone="accent">
                  <Icons.Key size={12} /> {table.primaryKey.name} ({table.primaryKey.columns.join(', ')})
                </Badge>
              ) : (
                <Badge tone="warning">No primary key · cannot be a selection root</Badge>
              )}
              <Badge>{table.columns.length} columns</Badge>
              <Badge>{outbound.length} outbound</Badge>
              <Badge>{inbound.length} inbound</Badge>
            </div>
          </div>
          {table.primaryKey && hasPermission('Selections.Write') ? (
            <Button
              icon={<Icons.Filter size={15} />}
              onClick={() => navigate(`/selections/new?connection=${connectionId}&snapshot=${snapshot.snapshotId}&table=${encodeURIComponent(key)}`)}
              variant="primary"
            >
              Select rows from this table
            </Button>
          ) : null}
        </div>
      </Card>

      <div className="grid gap-4 xl:grid-cols-2">
        <Card padded={false}>
          <div className="border-b border-border px-4 py-3 text-[13px] font-semibold text-fg">Columns</div>
          <DataTable>
            <thead>
              <tr>
                <th>Column</th>
                <th>Type</th>
                <th>Nullable</th>
              </tr>
            </thead>
            <tbody>
              {table.columns.map((column) => (
                <tr key={column.name}>
                  <td className="font-mono text-[12.5px]">
                    <span className="inline-flex items-center gap-1.5">
                      {pkColumns.has(column.name) ? <Icons.Key className="text-accent" size={12} /> : fkColumns.has(column.name) ? <Icons.Link className="text-info" size={12} /> : null}
                      {column.name}
                    </span>
                  </td>
                  <td className="font-mono text-[12.5px] text-fg-muted">{column.storeType}</td>
                  <td className="text-fg-muted">{column.isNullable ? 'yes' : 'no'}</td>
                </tr>
              ))}
            </tbody>
          </DataTable>
        </Card>

        <div className="grid gap-4">
          <RelationshipList
            description="Selecting rows here pulls in the referenced parent rows."
            foreignKeys={outbound}
            onSelectTable={onSelectTable}
            pick={(fk) => fk.parentTable}
            title="Depends on (outbound)"
          />
          <RelationshipList
            description="Child rows are never pulled in automatically."
            foreignKeys={inbound}
            onSelectTable={onSelectTable}
            pick={(fk) => fk.childTable}
            title="Referenced by (inbound)"
          />
        </div>
      </div>
      <p className="text-xs text-fg-faint">
        Dependency direction: a foreign key from <Code>orders.customer_id</Code> to <Code>customers.id</Code> means selecting orders pulls in customers, never the reverse.{' '}
        <Link className="text-accent underline" to="/selections/new">
          Learn by building a selection.
        </Link>
      </p>
    </div>
  );
}

function RelationshipList({
  title,
  description,
  foreignKeys,
  pick,
  onSelectTable,
}: Readonly<{
  title: string;
  description: string;
  foreignKeys: readonly SnapshotForeignKey[];
  pick: (fk: SnapshotForeignKey) => SchemaTableAddress;
  onSelectTable: (address: SchemaTableAddress) => void;
}>) {
  return (
    <Card padded={false}>
      <div className="border-b border-border px-4 py-3">
        <div className="text-[13px] font-semibold text-fg">
          {title} <span className="ml-1 text-fg-faint">{foreignKeys.length}</span>
        </div>
        <div className="text-xs text-fg-muted">{description}</div>
      </div>
      {foreignKeys.length === 0 ? (
        <p className="px-4 py-4 text-sm text-fg-muted">None.</p>
      ) : (
        <ul className="divide-y divide-border">
          {foreignKeys.map((fk) => {
            const other = pick(fk);
            return (
              <li className="px-4 py-3" key={fk.name}>
                <div className="flex items-center justify-between gap-3">
                  <button className="truncate text-left text-sm font-semibold text-accent hover:underline" onClick={() => onSelectTable(other)} type="button">
                    {other.schema}.{other.name}
                  </button>
                  <div className="flex shrink-0 gap-1">
                    <Badge className="!h-5 !px-1.5 !text-[10px]" tone={fk.isEnforced ? 'success' : 'warning'}>
                      {fk.isEnforced ? 'enforced' : 'not enforced'}
                    </Badge>
                    <Badge className="!h-5 !px-1.5 !text-[10px]" tone={fk.isTrusted ? 'success' : 'warning'}>
                      {fk.isTrusted ? 'trusted' : 'untrusted'}
                    </Badge>
                  </div>
                </div>
                <div className="mt-1 font-mono text-[11.5px] text-fg-muted">
                  {fk.name} · {fk.childColumns.join(', ')} → {fk.parentColumns.join(', ')}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
