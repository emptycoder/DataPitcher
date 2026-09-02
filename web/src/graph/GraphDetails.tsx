import type { GraphTable } from './model';
import { presentGraphState } from './presentation';

export type GraphDetailsProps = Readonly<{
  table: GraphTable | null;
  onExpandDependencies: (tableId: string) => void;
  onExpandDependants: (tableId: string) => void;
}>;

export function GraphDetails({ table, onExpandDependencies, onExpandDependants }: GraphDetailsProps) {
  if (!table) return null;
  const state = presentGraphState(table.state);
  const qualifiedName = `${table.schema}.${table.name}`;

  return (
    <aside aria-labelledby="graph-details-heading">
      <h2 id="graph-details-heading">Details for {qualifiedName}</h2>
      <p><span aria-hidden="true">{state.icon}</span> {state.label}</p>
      <p>{qualifiedName}</p>
      <button type="button" onClick={() => onExpandDependencies(table.id)}>Expand dependencies</button>
      <button type="button" onClick={() => onExpandDependants(table.id)}>Expand dependants</button>
      <p>Expanding dependants only reveals schema context and does not select or transfer those rows.</p>
    </aside>
  );
}
