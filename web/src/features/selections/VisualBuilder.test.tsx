import { expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { SelectionWorkbench } from './SelectionWorkbench';
import { VisualBuilder } from './VisualBuilder';
import type { Predicate, SelectionSchema, VisualSelection } from './selectionAst';
import { createWorkbenchPreferences } from './workbenchPreferences';

const schema: SelectionSchema = {
  tables: [
    { tableId: 'sales.orders', stableKey: ['id', 'tenant_id'], columns: [{ name: 'id', valueKind: 'int' }, { name: 'title', valueKind: 'string' }, { name: 'created', valueKind: 'date' }, { name: 'at', valueKind: 'time' }] },
    { tableId: 'sales.customers', stableKey: ['id'], columns: [{ name: 'id', valueKind: 'int' }] },
    { tableId: 'sales.lines', stableKey: ['id'], columns: [{ name: 'order_id', valueKind: 'int' }] },
  ],
  foreignKeys: [
    { foreignKeyId: 'fk_orders_customer', childTableId: 'sales.orders', parentTableId: 'sales.customers' },
    { foreignKeyId: 'fk_lines_orders', childTableId: 'sales.lines', parentTableId: 'sales.orders' },
  ],
};

const selection: VisualSelection = { root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id', 'tenant_id'] }, joins: [], predicate: null };

it('edits supplied AST members with typed values and requests snapshots through its parent', () => {
  const onChange = vi.fn();
  const onRequestSqlSnapshot = vi.fn();
  const { unmount } = render(<VisualBuilder selection={selection} schema={schema} validationMessages={[]} onChange={onChange} onRequestSqlSnapshot={onRequestSqlSnapshot} />);

  expect(screen.getByText('id, tenant_id')).toBeVisible();
  for (const name of ['Add AND group', 'Add OR group', 'Negate condition', 'Between', 'In list', 'Is null', 'Contains', 'Starts with', 'Ends with', 'Date range', 'Time range', 'Add known relationship', 'Add reverse relationship', 'Add manual join', 'Add exists']) {
    fireEvent.click(screen.getByRole('button', { name }));
  }
  fireEvent.click(screen.getByRole('button', { name: 'Request SQL snapshot' }));

  expect(onChange).toHaveBeenCalledTimes(15);
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'between', lower: expect.objectContaining({ kind: 'int' }), upper: expect.objectContaining({ kind: 'int' }) }) }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'set', values: [expect.objectContaining({ kind: 'int' })] }) }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'text', match: 'contains', value: expect.objectContaining({ kind: 'string' }) }) }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'temporalRange', temporalKind: 'date', lower: expect.objectContaining({ kind: 'date' }) }) }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'exists', correlations: [expect.objectContaining({ outer: expect.objectContaining({ valueKind: 'int' }) })] }) }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ joins: [expect.objectContaining({ kind: 'foreignKey', direction: 'forward' })] }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ joins: [expect.objectContaining({ kind: 'foreignKey', direction: 'reverse' })] }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ joins: [expect.objectContaining({ kind: 'manual', pairs: [{ fromColumn: 'id', toColumn: 'id' }] })] }));
  expect(onRequestSqlSnapshot).toHaveBeenCalledOnce();
  unmount();
});

it('renders validation messages and prevents an invalid snapshot request', () => {
  const onRequestSqlSnapshot = vi.fn();
  const { unmount } = render(<VisualBuilder selection={selection} schema={schema} validationMessages={['Root stable key must match the schema order.']} onChange={vi.fn()} onRequestSqlSnapshot={onRequestSqlSnapshot} />);

  expect(screen.getByRole('alert')).toHaveTextContent('Root stable key must match the schema order.');
  expect(screen.getByRole('button', { name: 'Request SQL snapshot' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Request SQL snapshot' }));
  expect(onRequestSqlSnapshot).not.toHaveBeenCalled();
  unmount();
});

it('filters operators and renders nested AST predicate variants', () => {
  const predicate: Predicate = {
    kind: 'and', terms: [
      { kind: 'comparison', column: { alias: 'o', name: 'id', valueKind: 'int' }, operator: 'equal', value: { kind: 'int', value: 1 } },
      { kind: 'between', column: { alias: 'o', name: 'id', valueKind: 'int' }, lower: { kind: 'int', value: 1 }, upper: { kind: 'int', value: 2 } },
      { kind: 'set', column: { alias: 'o', name: 'id', valueKind: 'int' }, negated: false, values: [{ kind: 'int', value: 1 }] },
      { kind: 'null', column: { alias: 'o', name: 'id', valueKind: 'int' }, negated: false },
      { kind: 'text', column: { alias: 'o', name: 'title', valueKind: 'string' }, match: 'contains', value: { kind: 'string', value: 'A' } },
      { kind: 'boolean', column: { alias: 'o', name: 'id', valueKind: 'int' }, value: { kind: 'int', value: 1 } },
      { kind: 'temporalRange', column: { alias: 'o', name: 'created', valueKind: 'date' }, temporalKind: 'date', lower: { kind: 'date', value: '2026-09-01' }, upper: { kind: 'date', value: '2026-09-02' } },
      { kind: 'exists', tableId: 'sales.lines', alias: 'l', correlations: [{ outer: { alias: 'o', name: 'id', valueKind: 'int' }, innerColumn: 'order_id' }], predicate: { kind: 'not', term: { kind: 'or', terms: [{ kind: 'comparison', column: { alias: 'o', name: 'id', valueKind: 'int' }, operator: 'equal', value: { kind: 'int', value: 1 } }, { kind: 'comparison', column: { alias: 'o', name: 'id', valueKind: 'int' }, operator: 'equal', value: { kind: 'int', value: 2 } }] } }, negated: false },
    ],
  };
  const onChange = vi.fn();
  const { unmount } = render(<VisualBuilder selection={{ ...selection, predicate }} schema={schema} validationMessages={[]} onChange={onChange} onRequestSqlSnapshot={vi.fn()} />);

  fireEvent.change(screen.getByRole('combobox', { name: 'Column' }), { target: { value: 'title' } });
  fireEvent.change(screen.getByRole('textbox', { name: 'Value' }), { target: { value: 'needle' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add AND group' }));

  expect(screen.getByRole('option', { name: 'contains' })).toBeVisible();
  expect(screen.queryByRole('option', { name: 'between' })).toBeNull();
  expect(screen.getByRole('group', { name: 'NOT' })).toBeVisible();
  expect(screen.getByRole('group', { name: 'EXISTS' })).toBeVisible();
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'and', terms: [predicate, expect.objectContaining({ column: expect.objectContaining({ name: 'title', valueKind: 'string' }), value: { kind: 'string', value: 'needle' } })] }) }));
  unmount();
});

it('passes the AST, schema, and snapshot request through the workbench parent', () => {
  const onVisualChange = vi.fn();
  const onRequestSqlSnapshot = vi.fn();
  const preferences = createWorkbenchPreferences({ getItem: () => null, setItem: () => {}, removeItem: () => {} });
  const { unmount } = render(
    <SelectionWorkbench
      tables={[{ tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id', 'tenant_id'], selected: true }]}
      root={{ tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id', 'tenant_id'], selected: true }}
      selectionName="Orders"
      activeTab="visual"
      preferences={preferences}
      onSelectRoot={vi.fn()}
      onShowColumns={vi.fn()}
      onSelectionNameChange={vi.fn()}
      onTabChange={vi.fn()}
      rightRail={null}
      selection={selection}
      schema={schema}
      onVisualChange={onVisualChange}
      onRequestSqlSnapshot={onRequestSqlSnapshot}
    />,
  );

  fireEvent.click(screen.getByRole('button', { name: 'Between' }));
  fireEvent.click(screen.getByRole('button', { name: 'Request SQL snapshot' }));
  expect(onVisualChange).toHaveBeenCalledWith(expect.objectContaining({ predicate: expect.objectContaining({ kind: 'between' }) }));
  expect(onRequestSqlSnapshot).toHaveBeenCalledOnce();
  unmount();
});

it('renders the builder only in the visual workbench tab', () => {
  const preferences = createWorkbenchPreferences({ getItem: () => null, setItem: () => {}, removeItem: () => {} });
  render(
    <SelectionWorkbench
      tables={[{ tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id', 'tenant_id'], selected: true }]}
      root={{ tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 1, stableKeyColumns: ['id', 'tenant_id'], selected: true }}
      selectionName="Orders"
      activeTab="sql"
      preferences={preferences}
      onSelectRoot={vi.fn()}
      onShowColumns={vi.fn()}
      onSelectionNameChange={vi.fn()}
      onTabChange={vi.fn()}
      rightRail={null}
      selection={selection}
      schema={schema}
      onVisualChange={vi.fn()}
      onRequestSqlSnapshot={vi.fn()}
    />,
  );

  expect(screen.queryByRole('region', { name: 'Visual builder' })).toBeNull();
});
