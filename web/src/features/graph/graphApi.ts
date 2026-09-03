import { z } from 'zod';
import { requestJson } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import type { SchemaGraphProjection, SchemaTableAddress } from './graphLayout';

const tableAddress = z.object({ schema: z.string(), name: z.string() });
const planGraphResponse = z.object({
  tables: z.array(z.object({ id: z.string(), schema: z.string(), name: z.string() })),
  relationships: z.array(z.object({ name: z.string(), childTableId: z.string(), parentTableId: z.string() })),
});
const snapshotResponse = z.object({
  tables: z.array(tableAddress),
  foreignKeys: z.array(z.object({ name: z.string(), childTable: tableAddress, parentTable: tableAddress })),
});
const snapshotSummaries = z.array(z.object({ snapshotId: z.string() }));

export async function fetchPlanGraph(planId: string, authentication: AuthenticationAdapter, signal: AbortSignal): Promise<SchemaGraphProjection> {
  return projectPlanGraph(planGraphResponse.parse(await requestJson<unknown>(`/api/plans/${planId}/schema-dependency-graph`, authentication, { signal })));
}

export async function fetchSnapshotGraph(connectionId: string, snapshotId: string, authentication: AuthenticationAdapter, signal: AbortSignal): Promise<SchemaGraphProjection> {
  return projectSnapshotGraph(snapshotResponse.parse(await requestJson<unknown>(`/api/connections/${connectionId}/snapshots/${snapshotId}`, authentication, { signal })));
}

export async function fetchLatestSnapshotGraph(connectionId: string, authentication: AuthenticationAdapter, signal: AbortSignal): Promise<SchemaGraphProjection | null> {
  const snapshots = snapshotSummaries.parse(await requestJson<unknown>(`/api/connections/${connectionId}/snapshots`, authentication, { signal }));
  return snapshots[0] === undefined ? null : fetchSnapshotGraph(connectionId, snapshots[0].snapshotId, authentication, signal);
}

function projectPlanGraph(graph: z.infer<typeof planGraphResponse>): SchemaGraphProjection {
  const tables = new Map<string, SchemaTableAddress>(graph.tables.map((table) => [table.id, { schema: table.schema, name: table.name }]));
  return {
    tables: [...tables.values()],
    edges: graph.relationships.map((relationship) => ({ child: tables.get(relationship.childTableId)!, parent: tables.get(relationship.parentTableId)!, foreignKeyName: relationship.name })),
  };
}

function projectSnapshotGraph(snapshot: z.infer<typeof snapshotResponse>): SchemaGraphProjection {
  return {
    tables: snapshot.tables,
    edges: snapshot.foreignKeys.map((foreignKey) => ({ child: foreignKey.childTable, parent: foreignKey.parentTable, foreignKeyName: foreignKey.name })),
  };
}
