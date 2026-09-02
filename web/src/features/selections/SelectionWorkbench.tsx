import { useState, type ReactNode } from 'react';
import { SchemaBrowser, type SelectionTableSummary } from './SchemaBrowser';
import { validateVisualSelection, type SelectionSchema, type VisualSelection } from './selectionAst';
import { VisualBuilder } from './VisualBuilder';
import type { createWorkbenchPreferences } from './workbenchPreferences';

type WorkbenchPreferences = ReturnType<typeof createWorkbenchPreferences>;
type WorkbenchTab = 'visual' | 'sql' | 'preview' | 'explain';
type SelectionWorkbenchProps = {
  tables: readonly SelectionTableSummary[];
  root: SelectionTableSummary & { stableKeyColumns: readonly string[] };
  selectionName: string;
  activeTab: WorkbenchTab;
  preferences: WorkbenchPreferences;
  onSelectRoot: (table: SelectionTableSummary) => void;
  onShowColumns: (table: SelectionTableSummary) => void;
  onSelectionNameChange: (selectionName: string) => void;
  onTabChange: (tab: WorkbenchTab) => void;
  rightRail: ReactNode;
  selection: VisualSelection;
  schema: SelectionSchema;
  onVisualChange: (selection: VisualSelection) => void;
  onRequestSqlSnapshot: () => void;
};

const tabs: readonly { name: string; value: WorkbenchTab }[] = [
  { name: 'Visual Builder', value: 'visual' },
  { name: 'SQL', value: 'sql' },
  { name: 'Preview', value: 'preview' },
  { name: 'Explain', value: 'explain' },
];

export function SelectionWorkbench({ tables, root, selectionName, activeTab, preferences, onSelectRoot, onShowColumns, onSelectionNameChange, onTabChange, rightRail, selection, schema, onVisualChange, onRequestSqlSnapshot }: SelectionWorkbenchProps) {
  const [search, setSearch] = useState('');
  const validationMessages = validateVisualSelection(selection, schema);

  return (
    <section style={{ display: 'grid', gridTemplateColumns: 'minmax(16rem, 22rem) minmax(32rem, 1fr) minmax(18rem, 24rem)' }}>
      <aside aria-label="Schema browser">
        <SchemaBrowser tables={tables} search={search} preferences={preferences} onSearchChange={setSearch} onSelectRoot={onSelectRoot} onShowColumns={onShowColumns} />
      </aside>
      <section aria-label="Selection editor">
        <header>
          <h1>{`${root.schemaName}.${root.tableName}`}</h1>
          <p>{root.stableKeyColumns.join(', ')}</p>
          <label>
            Selection name
            <input value={selectionName} onChange={(event) => onSelectionNameChange(event.target.value)} />
          </label>
          <nav aria-label="Selection workbench tabs">
            {tabs.map((tab) => <button key={tab.value} type="button" aria-pressed={activeTab === tab.value} onClick={() => onTabChange(tab.value)}>{tab.name}</button>)}
          </nav>
        </header>
        {activeTab === 'visual' ? <VisualBuilder selection={selection} schema={schema} validationMessages={validationMessages} onChange={onVisualChange} onRequestSqlSnapshot={onRequestSqlSnapshot} /> : null}
      </section>
      <aside aria-label="Selection cart">{rightRail}</aside>
    </section>
  );
}
