import { expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { SelectionWorkbench } from './SelectionWorkbench';
import { createWorkbenchPreferences } from './workbenchPreferences';

const tables = [
  { tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 12500, stableKeyColumns: ['id', 'tenant_id'], selected: true },
  { tableId: 'sales.blocked', schemaName: 'sales', tableName: 'Blocked', approximateRowCount: null, stableKeyColumns: null, selected: false },
] as const;

it('renders searchable schema actions in an accessible desktop workbench', () => {
  const values = new Map<string, string>();
  const preferences = createWorkbenchPreferences({
    getItem: (name) => values.get(name) ?? null,
    setItem: (name, value) => { values.set(name, value); },
    removeItem: (name) => { values.delete(name); },
  });
  preferences.actions.recordRecent('sales.orders');
  const onSelectRoot = vi.fn();
  const onShowColumns = vi.fn();
  const onSelectionNameChange = vi.fn();
  const onTabChange = vi.fn();

  render(
    <SelectionWorkbench
      tables={tables}
      root={tables[0]}
      selectionName="Orders to move"
      activeTab="visual"
      preferences={preferences}
      onSelectRoot={onSelectRoot}
      onShowColumns={onShowColumns}
      onSelectionNameChange={onSelectionNameChange}
      onTabChange={onTabChange}
      rightRail={<p>Cart placeholder</p>}
      selection={{ root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id', 'tenant_id'] }, joins: [], predicate: null }}
      schema={{ tables: [{ tableId: 'sales.orders', stableKey: ['id', 'tenant_id'], columns: [{ name: 'id', valueKind: 'int' }, { name: 'tenant_id', valueKind: 'guid' }] }], foreignKeys: [] }}
      onVisualChange={vi.fn()}
      onRequestSqlSnapshot={vi.fn()}
    />,
  );

  expect(screen.getByRole('complementary', { name: 'Schema browser' })).toBeVisible();
  expect(screen.getByRole('region', { name: 'Selection editor' })).toBeVisible();
  expect(screen.getByRole('complementary', { name: 'Selection cart' })).toBeVisible();
  expect(screen.getByRole('button', { name: /Orders/ })).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByText('≈ 12,500 rows')).toBeVisible();
  expect(screen.getByText('Stable key unavailable')).toBeVisible();
  expect(screen.getAllByText('Recent')).toHaveLength(1);

  const selectRoot = screen.getAllByRole('button', { name: 'Select root' });
  fireEvent.click(selectRoot[0]!);
  expect(onSelectRoot).toHaveBeenCalledWith(tables[0]);
  expect(selectRoot[1]).toBeDisabled();
  fireEvent.click(selectRoot[1]!);
  expect(onSelectRoot).toHaveBeenCalledTimes(1);

  const showColumns = screen.getAllByRole('button', { name: 'Show columns' });
  fireEvent.click(showColumns[0]!);
  expect(onShowColumns).toHaveBeenCalledWith(tables[0]);

  const favourites = screen.getAllByRole('button', { name: 'Toggle favourite' });
  fireEvent.click(favourites[0]!);
  expect(favourites[0]).toHaveAttribute('aria-pressed', 'true');
  expect(JSON.parse(values.get('datapitcher.selection-workbench')!).state.favouriteTableIds).toEqual(['sales.orders']);
  fireEvent.click(favourites[0]!);
  expect(favourites[0]).toHaveAttribute('aria-pressed', 'false');

  fireEvent.change(screen.getByRole('searchbox', { name: 'Search schema' }), { target: { value: 'orders' } });
  expect(screen.getByRole('button', { name: /Orders/ })).toBeVisible();
  expect(screen.queryByRole('button', { name: /Blocked/ })).toBeNull();
  act(() => {
    preferences.actions.recordRecent('sales.blocked');
    preferences.actions.recordRecent('sales.orders');
  });
  expect(JSON.parse(values.get('datapitcher.selection-workbench')!).state.recentTableIds).toEqual(['sales.orders', 'sales.blocked']);

  fireEvent.change(screen.getByRole('textbox', { name: 'Selection name' }), { target: { value: 'Renamed' } });
  fireEvent.click(screen.getByRole('button', { name: 'SQL' }));
  expect(onSelectionNameChange).toHaveBeenCalledWith('Renamed');
  expect(onTabChange).toHaveBeenCalledWith('sql');
});
