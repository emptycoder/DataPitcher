import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { SchemaGraphProjection } from './graphLayout';

afterEach(cleanup);

const graph: SchemaGraphProjection = {
    tables: [
        { schema: 'sales', name: 'customers' },
        { schema: 'sales', name: 'orders' },
    ],
    edges: [
        {
            child: { schema: 'sales', name: 'orders' },
            parent: { schema: 'sales', name: 'customers' },
            foreignKeyName: 'FK_orders_customers',
        },
    ],
};

it('renders table boxes and child-to-parent relationships in an accessible SVG', async () => {
    const { GraphView } = await import('./GraphView');
    render(<GraphView graph={graph} />);

    const svg = screen.getByRole('group', { name: 'Schema dependency graph' });
    expect(svg).toHaveAttribute('viewBox', '0 0 400 64');
    expect(screen.getByText('customers')).toBeVisible();
    expect(screen.getByText('orders')).toBeVisible();
    expect(screen.getByTestId('edge-FK_orders_customers')).toHaveAttribute('marker-end');
    expect(svg.querySelector('desc')).toHaveTextContent('sales.orders references sales.customers');
});

it('shows an explicit empty state when there are no tables', async () => {
    const { GraphView } = await import('./GraphView');
    render(<GraphView graph={{ tables: [], edges: [] }} />);

    expect(screen.getByRole('status')).toHaveTextContent('No tables to display.');
});

it('draws back-edges with a dashed line', async () => {
    const { GraphView } = await import('./GraphView');
    render(
        <GraphView
            graph={{
                tables: [
                    { schema: 'sales', name: 'accounts' },
                    { schema: 'sales', name: 'profiles' },
                ],
                edges: [
                    {
                        child: { schema: 'sales', name: 'accounts' },
                        parent: { schema: 'sales', name: 'profiles' },
                        foreignKeyName: 'FK_accounts_profiles',
                    },
                    {
                        child: { schema: 'sales', name: 'profiles' },
                        parent: { schema: 'sales', name: 'accounts' },
                        foreignKeyName: 'FK_profiles_accounts',
                    },
                ],
            }}
        />,
    );

    expect(screen.getByTestId('edge-FK_profiles_accounts')).toHaveAttribute('stroke-dasharray', '6 4');
});

it('highlights the selected table', async () => {
    const { GraphView } = await import('./GraphView');
    render(<GraphView graph={graph} selectedTable={{ schema: 'sales', name: 'orders' }} />);

    expect(screen.getByTestId('table-sales.orders')).toHaveAttribute('data-selected', 'true');
});

it('notifies the caller when a table is selected by mouse or keyboard', async () => {
    const { GraphView } = await import('./GraphView');
    const onSelectTable = vi.fn();
    render(<GraphView graph={graph} onSelectTable={onSelectTable} />);

    const orders = screen.getByRole('button', { name: 'sales.orders' });
    fireEvent.click(orders);
    fireEvent.keyDown(orders, { key: 'Enter' });
    fireEvent.keyDown(orders, { key: ' ' });
    fireEvent.keyDown(orders, { key: 'Escape' });

    expect(onSelectTable).toHaveBeenCalledTimes(3);
    expect(onSelectTable).toHaveBeenCalledWith({ schema: 'sales', name: 'orders' });
});
