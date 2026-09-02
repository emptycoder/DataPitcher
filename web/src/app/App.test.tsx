import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';

vi.mock('../graph/elkLayout', () => ({
  layoutOptions: { version: 'dependency-graph-v1' },
  createElkLayoutAdapter: () => ({ layout: async (key: string) => ({ key, positions: {}, edgeSections: {} }), dispose: vi.fn() }),
}));

vi.mock('../graph/DependencyGraphView', async () => {
  const { createElement, useEffect } = await import('react');
  return {
    DependencyGraphView: ({ items, onMeasure }: Readonly<{ items: readonly Readonly<{ id: string; memberIds: readonly string[] }>[]; onMeasure: (itemId: string, size: Readonly<{ width: number; height: number }>) => void }>) => {
      useEffect(() => { items.forEach((item) => onMeasure(item.id, { width: 180, height: 60 })); }, [items, onMeasure]);
      return createElement('output', { 'aria-label': 'Visible tables' }, items.map((item) => item.memberIds.join(',')).join(','));
    },
  };
});

import { App } from './App';
import { AppProviders } from './AppProviders';

afterEach(() => {
  cleanup();
  window.history.replaceState(null, '', '/');
  vi.restoreAllMocks();
});

it('renders the application landmark and name', () => {
  render(<App />);
  expect(screen.getByRole('main')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'DataPitcher' })).toBeVisible();
});

it('hosts the graph route without fabricating a selected plan', () => {
  window.history.replaceState(null, '', '/dependency-graph');
  render(<AppProviders><App /></AppProviders>);

  expect(screen.getByRole('link', { name: 'Dependency graph' })).toHaveAttribute('href', '/dependency-graph');
  expect(screen.getByRole('status')).toHaveTextContent('Choose a transfer plan to view its dependencies.');
});

it('uses the route plan as the graph context', async () => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
    revision: 'r1', plannedTableIds: ['orders'], tables: [
      { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'root-selected' },
      { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'required-dependency' },
    ], relationships: [{ id: 'orders-customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' }],
  }), { status: 200 }));
  window.history.replaceState(null, '', '/dependency-graph/plan-1');
  render(<AppProviders><App /></AppProviders>);

  expect(await screen.findByLabelText('Visible tables')).toHaveTextContent('customers,orders');
});
