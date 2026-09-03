import { expect, it } from 'vitest';
import { layoutSchemaGraph, type GraphLayout, type SchemaGraphProjection } from './graphLayout';

function nodeFor(layout: GraphLayout, name: string) {
    return layout.nodes.find((node) => node.table.name === name)!;
}

it('returns empty geometry for an empty schema graph', () => {
    expect(layoutSchemaGraph({ tables: [], edges: [] })).toEqual({
        nodes: [],
        edges: [],
        bounds: { x: 0, y: 0, width: 0, height: 0 },
    });
});

it('layers dependants after dependencies and is deterministic', () => {
    const graph: SchemaGraphProjection = {
        tables: [
            { schema: 'sales', name: 'order_lines' },
            { schema: 'sales', name: 'orders' },
            { schema: 'sales', name: 'customers' },
        ],
        edges: [
            {
                child: { schema: 'sales', name: 'orders' },
                parent: { schema: 'sales', name: 'customers' },
                foreignKeyName: 'FK_orders_customers',
            },
            {
                child: { schema: 'sales', name: 'order_lines' },
                parent: { schema: 'sales', name: 'orders' },
                foreignKeyName: 'FK_lines_orders',
            },
        ],
    };
    const layout = layoutSchemaGraph(graph);

    expect(nodeFor(layout, 'customers').layer).toBe(0);
    expect(nodeFor(layout, 'orders').layer).toBe(1);
    expect(nodeFor(layout, 'order_lines').layer).toBe(2);
    expect(layout.edges.every((edge) => edge.points.length === 2 && !edge.isBackEdge)).toBe(true);
    expect(layoutSchemaGraph({ tables: [...graph.tables].reverse(), edges: [...graph.edges].reverse() })).toEqual(
        layout,
    );
});

it('terminates and marks the broken edge in a two-table cycle', () => {
    const layout = layoutSchemaGraph({
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
    });

    expect(layout.nodes.map((node) => node.layer)).toEqual([0, 1]);
    expect(layout.edges.filter((edge) => edge.isBackEdge).map((edge) => edge.edge.foreignKeyName)).toEqual([
        'FK_profiles_accounts',
    ]);
});

it('terminates and marks the broken edge in a three-table cycle', () => {
    const layout = layoutSchemaGraph({
        tables: [
            { schema: 'sales', name: 'a' },
            { schema: 'sales', name: 'b' },
            { schema: 'sales', name: 'c' },
        ],
        edges: [
            { child: { schema: 'sales', name: 'a' }, parent: { schema: 'sales', name: 'b' }, foreignKeyName: 'FK_a_b' },
            { child: { schema: 'sales', name: 'b' }, parent: { schema: 'sales', name: 'c' }, foreignKeyName: 'FK_b_c' },
            { child: { schema: 'sales', name: 'c' }, parent: { schema: 'sales', name: 'a' }, foreignKeyName: 'FK_c_a' },
        ],
    });

    expect(layout.nodes.map((node) => node.layer)).toEqual([0, 1, 2]);
    expect(layout.edges.filter((edge) => edge.isBackEdge).map((edge) => edge.edge.foreignKeyName)).toEqual(['FK_c_a']);
});

it('marks a self-reference as a back-edge', () => {
    const layout = layoutSchemaGraph({
        tables: [{ schema: 'sales', name: 'employees' }],
        edges: [
            {
                child: { schema: 'sales', name: 'employees' },
                parent: { schema: 'sales', name: 'employees' },
                foreignKeyName: 'FK_employees_manager',
            },
        ],
    });

    expect(layout.nodes[0]?.layer).toBe(0);
    expect(layout.edges[0]?.isBackEdge).toBe(true);
    expect(layout.edges[0]?.points[0]).not.toEqual(layout.edges[0]?.points[1]);
});

it('places disconnected components without overlapping nodes', () => {
    const layout = layoutSchemaGraph({
        tables: [
            { schema: 'sales', name: 'customers' },
            { schema: 'sales', name: 'orders' },
            { schema: 'warehouse', name: 'bins' },
            { schema: 'warehouse', name: 'stock' },
        ],
        edges: [
            {
                child: { schema: 'sales', name: 'orders' },
                parent: { schema: 'sales', name: 'customers' },
                foreignKeyName: 'FK_orders_customers',
            },
            {
                child: { schema: 'warehouse', name: 'stock' },
                parent: { schema: 'warehouse', name: 'bins' },
                foreignKeyName: 'FK_stock_bins',
            },
        ],
    });

    expect(layout.nodes).toHaveLength(4);
    for (const [index, node] of layout.nodes.entries()) {
        for (const other of layout.nodes.slice(index + 1)) {
            expect(
                node.x + node.width <= other.x ||
                    other.x + other.width <= node.x ||
                    node.y + node.height <= other.y ||
                    other.y + other.height <= node.y,
            ).toBe(true);
        }
    }
});
