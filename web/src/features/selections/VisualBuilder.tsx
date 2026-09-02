import { useState } from 'react';
import { addJoin, operatorsFor, replacePredicate, type ColumnRef, type Predicate, type SelectionSchema, type TypedValue, type ValueKind, type VisualSelection } from './selectionAst';

type VisualBuilderProps = {
  selection: VisualSelection;
  schema: SelectionSchema;
  validationMessages: readonly string[];
  onChange: (selection: VisualSelection) => void;
  onRequestSqlSnapshot: () => void;
};

const defaultValues: Record<ValueKind, string | number | boolean> = { int: 0, decimal: 0, string: '', boolean: false, date: '', time: '', dateTime: '', guid: '' };

export function VisualBuilder({ selection, schema, validationMessages, onChange, onRequestSqlSnapshot }: VisualBuilderProps) {
  const root = schema.tables.find((table) => table.tableId === selection.root.tableId)!;
  const [columnName, setColumnName] = useState(root.columns[0]!.name);
  const [inputValue, setInputValue] = useState('');
  const selectedColumn = root.columns.find((column) => column.name === columnName)!;
  const column = (valueKind: ValueKind): ColumnRef => ({ alias: selection.root.alias, name: root.columns.find((item) => item.valueKind === valueKind)!.name, valueKind });
  const value = (valueKind: ValueKind, text = defaultValues[valueKind]): TypedValue => ({ kind: valueKind, value: text });
  const comparison = (): Predicate => ({ kind: 'comparison', column: { alias: selection.root.alias, name: selectedColumn.name, valueKind: selectedColumn.valueKind }, operator: 'equal', value: value(selectedColumn.valueKind, inputValue) });
  const current = selection.predicate ?? comparison();
  const changePredicate = (predicate: Predicate) => onChange(replacePredicate(selection, predicate));
  const forward = schema.foreignKeys.find((item) => item.childTableId === root.tableId);
  const reverse = schema.foreignKeys.find((item) => item.parentTableId === root.tableId);
  const target = schema.tables.find((table) => table.tableId !== root.tableId);
  const matchingTargetColumn = target?.columns.find((item) => item.valueKind === 'int');

  return (
    <section aria-label="Visual builder">
      <p>{selection.root.stableKey.join(', ')}</p>
      <label>
        Column
        <select aria-label="Column" value={columnName} onChange={(event) => setColumnName(event.target.value)}>
          {root.columns.map((item) => <option key={item.name} value={item.name}>{item.name}</option>)}
        </select>
      </label>
      <label>
        Operator
        <select aria-label="Operator">
          {operatorsFor(selectedColumn.valueKind).map((operator) => <option key={operator}>{operator}</option>)}
        </select>
      </label>
      <label>
        Value
        <input aria-label="Value" value={inputValue} onChange={(event) => setInputValue(event.target.value)} />
      </label>
      <PredicateView predicate={selection.predicate} />
      <div>
        <button type="button" onClick={() => changePredicate({ kind: 'and', terms: [current, comparison()] })}>Add AND group</button>
        <button type="button" onClick={() => changePredicate({ kind: 'or', terms: [current, comparison()] })}>Add OR group</button>
        <button type="button" onClick={() => changePredicate({ kind: 'not', term: current })}>Negate condition</button>
        <button type="button" onClick={() => changePredicate({ kind: 'between', column: column('int'), lower: value('int'), upper: value('int') })}>Between</button>
        <button type="button" onClick={() => changePredicate({ kind: 'set', column: column('int'), negated: false, values: [value('int')] })}>In list</button>
        <button type="button" onClick={() => changePredicate({ kind: 'null', column: column('int'), negated: false })}>Is null</button>
        <button type="button" onClick={() => changePredicate({ kind: 'text', column: column('string'), match: 'contains', value: value('string') })}>Contains</button>
        <button type="button" onClick={() => changePredicate({ kind: 'text', column: column('string'), match: 'startsWith', value: value('string') })}>Starts with</button>
        <button type="button" onClick={() => changePredicate({ kind: 'text', column: column('string'), match: 'endsWith', value: value('string') })}>Ends with</button>
        <button type="button" onClick={() => changePredicate({ kind: 'temporalRange', column: column('date'), temporalKind: 'date', lower: value('date'), upper: value('date') })}>Date range</button>
        <button type="button" onClick={() => changePredicate({ kind: 'temporalRange', column: column('time'), temporalKind: 'time', lower: value('time'), upper: value('time') })}>Time range</button>
        <button type="button" disabled={forward === undefined} onClick={() => onChange(addJoin(selection, { kind: 'foreignKey', fromAlias: selection.root.alias, alias: 'joined', foreignKeyId: forward!.foreignKeyId, direction: 'forward' }))}>Add known relationship</button>
        <button type="button" disabled={reverse === undefined} onClick={() => onChange(addJoin(selection, { kind: 'foreignKey', fromAlias: selection.root.alias, alias: 'joined', foreignKeyId: reverse!.foreignKeyId, direction: 'reverse' }))}>Add reverse relationship</button>
        <button type="button" disabled={target === undefined || matchingTargetColumn === undefined} onClick={() => onChange(addJoin(selection, { kind: 'manual', fromAlias: selection.root.alias, tableId: target!.tableId, alias: 'joined', pairs: [{ fromColumn: column('int').name, toColumn: matchingTargetColumn!.name }] }))}>Add manual join</button>
        <button type="button" disabled={target === undefined || matchingTargetColumn === undefined} onClick={() => changePredicate({ kind: 'exists', tableId: target!.tableId, alias: 'exists', correlations: [{ outer: column('int'), innerColumn: matchingTargetColumn!.name }], predicate: null, negated: false })}>Add exists</button>
      </div>
      {validationMessages.length === 0 ? null : <ul role="alert">{validationMessages.map((message) => <li key={message}>{message}</li>)}</ul>}
      <button type="button" disabled={validationMessages.length > 0} onClick={onRequestSqlSnapshot}>Request SQL snapshot</button>
    </section>
  );
}

function PredicateView({ predicate }: { predicate: Predicate | null }) {
  if (predicate === null) return <p>No condition</p>;

  switch (predicate.kind) {
    case 'and':
    case 'or':
      return <fieldset><legend>{predicate.kind.toUpperCase()}</legend>{predicate.terms.map((term, index) => <PredicateView key={index} predicate={term} />)}</fieldset>;
    case 'not':
      return <fieldset><legend>NOT</legend><PredicateView predicate={predicate.term} /></fieldset>;
    case 'comparison':
      return <fieldset><legend>Comparison</legend>{predicate.column.name}</fieldset>;
    case 'between':
      return <fieldset><legend>Between</legend>{predicate.column.name}</fieldset>;
    case 'set':
      return <fieldset><legend>In list</legend>{predicate.column.name}</fieldset>;
    case 'null':
      return <fieldset><legend>Null</legend>{predicate.column.name}</fieldset>;
    case 'text':
      return <fieldset><legend>{predicate.match}</legend>{predicate.column.name}</fieldset>;
    case 'boolean':
      return <fieldset><legend>Boolean</legend>{predicate.column.name}</fieldset>;
    case 'temporalRange':
      return <fieldset><legend>{predicate.temporalKind}</legend>{predicate.column.name}</fieldset>;
    case 'exists':
      return <fieldset><legend>EXISTS</legend><PredicateView predicate={predicate.predicate} /></fieldset>;
  }
}
