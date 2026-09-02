import type { createWorkbenchPreferences } from './workbenchPreferences';

export type SelectionTableSummary = {
  tableId: string;
  schemaName: string;
  tableName: string;
  approximateRowCount: number | null;
  stableKeyColumns: readonly string[] | null;
  selected: boolean;
};

type WorkbenchPreferences = ReturnType<typeof createWorkbenchPreferences>;
type SchemaBrowserProps = {
  tables: readonly SelectionTableSummary[];
  search: string;
  preferences: WorkbenchPreferences;
  onSearchChange: (search: string) => void;
  onSelectRoot: (table: SelectionTableSummary) => void;
  onShowColumns: (table: SelectionTableSummary) => void;
};

export function SchemaBrowser({ tables, search, preferences, onSearchChange, onSelectRoot, onShowColumns }: SchemaBrowserProps) {
  const normalizedSearch = search.toLocaleLowerCase();

  return (
    <section>
      <label>
        Search schema
        <input type="search" value={search} onChange={(event) => onSearchChange(event.target.value)} />
      </label>
      <ul>
        {tables.filter((table) => `${table.schemaName}.${table.tableName}`.toLocaleLowerCase().includes(normalizedSearch)).map((table) => (
          <li key={table.tableId}>
            <SchemaTableRow table={table} preferences={preferences} onSelectRoot={onSelectRoot} onShowColumns={onShowColumns} />
          </li>
        ))}
      </ul>
    </section>
  );
}

type SchemaTableRowProps = Omit<SchemaBrowserProps, 'tables' | 'search' | 'onSearchChange'> & { table: SelectionTableSummary };

function SchemaTableRow({ table, preferences, onSelectRoot, onShowColumns }: SchemaTableRowProps) {
  const favourite = preferences.useIsFavourite(table.tableId);
  const recent = preferences.useIsRecent(table.tableId);
  const hasStableKey = table.stableKeyColumns !== null;

  return (
    <article>
      <button type="button" aria-pressed={table.selected}>{`${table.schemaName}.${table.tableName}`}</button>
      <p>{table.approximateRowCount === null ? 'Count unavailable' : `≈ ${table.approximateRowCount.toLocaleString('en-US')} rows`}</p>
      {hasStableKey ? null : <p>Stable key unavailable</p>}
      {recent ? <p>Recent</p> : null}
      <div aria-label={`${table.tableName} actions`}>
        <button type="button" disabled={!hasStableKey} onClick={hasStableKey ? () => {
          preferences.actions.recordRecent(table.tableId);
          onSelectRoot(table);
        } : undefined}>
          Select root
        </button>
        <button type="button" aria-pressed={favourite} onClick={() => preferences.actions.toggleFavourite(table.tableId)}>Toggle favourite</button>
        <button type="button" onClick={() => onShowColumns(table)}>Show columns</button>
      </div>
    </article>
  );
}
