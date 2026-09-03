import { useMutation, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { SelectionWorkbench } from './SelectionWorkbench';
import type { SelectionSchema, VisualSelection } from './selectionAst';
import { compileSelection, fetchSelectionWorkbenchSchema, type RequestFunction } from './workbenchApi';
import { createWorkbenchPreferences } from './workbenchPreferences';

export type SelectionWorkbenchScreenProps = Readonly<{ request: RequestFunction; authentication: AuthenticationAdapter }>;

export function SelectionWorkbenchScreen({ request, authentication }: SelectionWorkbenchScreenProps) {
  const schemaQuery = useQuery({ queryKey: ['selection-workbench-schema'], queryFn: ({ signal }) => fetchSelectionWorkbenchSchema(request, authentication, signal) });
  const preferences = useMemo(() => createWorkbenchPreferences(createMemoryStorage()), []);
  const [rootId, setRootId] = useState<string | null>(null);
  const [selectionName, setSelectionName] = useState('New selection');
  const [tab, setTab] = useState<'visual' | 'sql' | 'preview' | 'explain'>('visual');
  const [visual, setVisual] = useState<VisualSelection | null>(null);
  const [columnsFor, setColumnsFor] = useState<string | null>(null);
  const compile = useMutation({
    mutationFn: (selection: VisualSelection) => compileSelection(request, authentication, { mode: 'visual', visual: selection, rawSql: null, parameters: [], schemaRevision: schemaQuery.data!.schemaRevision }, new AbortController().signal),
  });

  if (schemaQuery.isPending) return <p role="status">Loading selection workbench.</p>;
  if (schemaQuery.isError || !schemaQuery.data) return <p role="status">Unable to load selection workbench.</p>;
  const root = schemaQuery.data.tables.find((table) => table.tableId === rootId) ?? schemaQuery.data.tables.find((table) => table.stableKeyColumns !== null);
  if (!root || root.stableKeyColumns === null) return <p role="status">No table with a stable key is available for selection.</p>;
  const stableKeyColumns = root.stableKeyColumns;
  const selection = visual?.root.tableId === root.tableId ? visual : { root: { tableId: root.tableId, alias: 'root', stableKey: stableKeyColumns }, joins: [], predicate: null };
  const schema: SelectionSchema = {
    tables: schemaQuery.data.tables.map((table) => ({ tableId: table.tableId, stableKey: table.stableKeyColumns, columns: table.columns })),
    foreignKeys: schemaQuery.data.foreignKeys,
  };
  const tables = schemaQuery.data.tables.map((table) => ({ ...table, selected: table.tableId === root.tableId }));
  const selectedColumns = schemaQuery.data.tables.find((table) => table.tableId === columnsFor);

  return (
    <section aria-label="Selection workbench">
      <SelectionWorkbench
        tables={tables}
        root={{ tableId: root.tableId, schemaName: root.schemaName, tableName: root.tableName, approximateRowCount: root.approximateRowCount, stableKeyColumns, selected: true }}
        selectionName={selectionName}
        activeTab={tab}
        preferences={preferences}
        onSelectRoot={(table) => { setRootId(table.tableId); setVisual(null); }}
        onShowColumns={(table) => setColumnsFor(table.tableId)}
        onSelectionNameChange={setSelectionName}
        onTabChange={setTab}
        rightRail={selectedColumns ? <p>{`${selectedColumns.schemaName}.${selectedColumns.tableName}: ${selectedColumns.columns.map((column) => column.name).join(', ')}`}</p> : null}
        selection={selection}
        schema={schema}
        onVisualChange={setVisual}
        onRequestSqlSnapshot={() => compile.mutate(selection)}
      />
      {tab === 'sql' ? <output aria-label="Generated SQL">{compile.data?.sqlSnapshot ?? 'Request a SQL snapshot from the Visual Builder.'}</output> : null}
      {compile.isError ? <p role="alert">Unable to compile selection.</p> : null}
    </section>
  );
}

function createMemoryStorage() {
  const values = new Map<string, string>();
  return { getItem: (name: string) => values.get(name) ?? null, setItem: (name: string, value: string) => { values.set(name, value); }, removeItem: (name: string) => { values.delete(name); } };
}
