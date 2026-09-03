import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SchemaGraph, tableKey } from './SchemaGraph';

afterEach(cleanup);

const graph = {
  tables: [
    { schema: 'app', name: 'customers' },
    { schema: 'app', name: 'orders' },
  ],
  edges: [{ child: { schema: 'app', name: 'orders' }, parent: { schema: 'app', name: 'customers' }, foreignKeyName: 'FK_orders_customers' }],
};

describe('SchemaGraph', () => {
  it('renders every table and relationship and reports selection', () => {
    const onSelect = vi.fn();
    render(<SchemaGraph graph={graph} onSelect={onSelect} selectedKey={null} />);
    expect(screen.getByText('2 tables · 1 foreign keys')).toBeInTheDocument();
    fireEvent.click(screen.getByText('orders'));
    expect(onSelect).toHaveBeenCalledWith({ schema: 'app', name: 'orders' });
    expect(tableKey({ schema: 'app', name: 'orders' })).toBe('app.orders');
  });

  it('shows an empty state without tables', () => {
    render(<SchemaGraph graph={{ tables: [], edges: [] }} />);
    expect(screen.getByText('No tables to display.')).toBeInTheDocument();
  });
});
