import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { HttpError } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { DataTable, InlineError, LoadingIndicator } from '../../ui';
import { GraphView } from './GraphView';
import { fetchLatestSnapshotGraph, fetchPlanGraph, fetchSnapshotGraph } from './graphApi';
import type { SchemaGraphEdge, SchemaTableAddress } from './graphLayout';

export type GraphScreenProps = Readonly<{
  authentication: AuthenticationAdapter;
  planId?: string | null;
  connectionId?: string | null;
  snapshotId?: string | null;
}>;

type GraphSource =
  | Readonly<{ kind: 'plan'; planId: string }>
  | Readonly<{ kind: 'snapshot'; connectionId: string; snapshotId: string | null }>
  | null;

export function GraphScreen({ authentication, planId = null, connectionId = null, snapshotId = null }: GraphScreenProps) {
  const source: GraphSource = planId !== null ? { kind: 'plan', planId } : connectionId === null ? null : { kind: 'snapshot', connectionId, snapshotId };
  const { hasPermission } = usePermissions();
  const [selectedTable, setSelectedTable] = useState<SchemaTableAddress>();
  const query = useQuery({
    queryKey: source === null ? ['schema-graph'] : [source.kind, source.kind === 'plan' ? source.planId : source.connectionId, source.kind === 'snapshot' ? source.snapshotId : null],
    enabled: source !== null,
    queryFn: ({ signal }) => {
      const graphSource = source!;
      if (graphSource.kind === 'plan') return fetchPlanGraph(graphSource.planId, authentication, signal);
      return graphSource.snapshotId === null
        ? fetchLatestSnapshotGraph(graphSource.connectionId, authentication, signal)
        : fetchSnapshotGraph(graphSource.connectionId, graphSource.snapshotId, authentication, signal);
    },
  });

  if (source === null) return <p role="status">Choose a transfer plan or schema snapshot to view its dependencies.</p>;
  if (!hasPermission(source.kind === 'plan' ? 'Plans.Read' : 'Schema.Read')) return <section aria-label="Schema dependency graph"><InlineError>You do not have permission to view this schema graph.</InlineError></section>;
  if (query.isPending) return <LoadingIndicator label="Loading schema dependency graph." />;
  if (query.isError) return <section aria-label="Schema dependency graph"><InlineError>{errorMessage(source, query.error)}</InlineError></section>;
  if (query.data === null) return <section aria-label="Schema dependency graph"><p>No schema snapshots exist for this connection. Run a schema scan, then return to this graph.</p></section>;

  const relationships = selectedTable === undefined ? [] : query.data.edges.filter((edge) => relatesTo(edge, selectedTable));
  return (
    <section aria-label="Schema dependency graph">
      <h2>Schema dependency graph</h2>
      <GraphView graph={query.data} selectedTable={selectedTable} onSelectTable={setSelectedTable} />
      {selectedTable === undefined ? null : (
        <section aria-label="Immediate relationships">
          <h3>Immediate relationships</h3>
          {relationships.length === 0 ? <p>{`${label(selectedTable)} has no immediate relationships.`}</p> : (
            <DataTable>
              <thead><tr><th scope="col">Child</th><th scope="col">Foreign key</th><th scope="col">Parent</th></tr></thead>
              <tbody>{relationships.map((edge) => <tr key={`${label(edge.child)}-${label(edge.parent)}-${edge.foreignKeyName}`}><td>{label(edge.child)}</td><td>{edge.foreignKeyName}</td><td>{label(edge.parent)}</td></tr>)}</tbody>
            </DataTable>
          )}
        </section>
      )}
    </section>
  );
}

function errorMessage(source: Exclude<GraphSource, null>, error: unknown): string {
  if (error instanceof HttpError && error.status === 404) return source.kind === 'plan' ? 'This plan has no sealed source snapshot.' : 'This schema snapshot was not found.';
  return source.kind === 'plan' ? 'Unable to load the plan schema graph.' : 'Unable to load the schema snapshot.';
}

function relatesTo(edge: SchemaGraphEdge, table: SchemaTableAddress): boolean {
  return label(edge.child) === label(table) || label(edge.parent) === label(table);
}

function label(table: SchemaTableAddress): string {
  return `${table.schema}.${table.name}`;
}
