import { useId, type KeyboardEvent } from 'react';
import { layoutSchemaGraph, type SchemaGraphProjection, type SchemaTableAddress } from './graphLayout';

export type GraphViewProps = Readonly<{
    graph: SchemaGraphProjection;
    selectedTable?: SchemaTableAddress;
    onSelectTable?: (table: SchemaTableAddress) => void;
}>;

export function GraphView({ graph, selectedTable, onSelectTable }: GraphViewProps) {
    const layout = layoutSchemaGraph(graph);
    const id = useId().replaceAll(':', '');
    const selectedTableKey = selectedTable === undefined ? '' : tableKey(selectedTable);

    if (layout.nodes.length === 0) return <p role="status">No tables to display.</p>;

    const descriptionId = `schema-graph-description-${id}`;
    const markerId = `schema-graph-arrow-${id}`;

    return (
        <svg
            aria-describedby={descriptionId}
            aria-label="Schema dependency graph"
            role="group"
            viewBox={`${layout.bounds.x} ${layout.bounds.y} ${layout.bounds.width} ${layout.bounds.height}`}
        >
            <desc id={descriptionId}>{summary(graph)}</desc>
            <defs>
                <marker id={markerId} markerHeight="8" markerWidth="8" orient="auto" refX="7" refY="4">
                    <path d="M 0 0 L 8 4 L 0 8 z" />
                </marker>
            </defs>
            {layout.edges.map(({ edge, points, isBackEdge }) => (
                <line
                    key={`${tableKey(edge.child)}-${tableKey(edge.parent)}-${edge.foreignKeyName}`}
                    data-testid={`edge-${edge.foreignKeyName}`}
                    markerEnd={`url(#${markerId})`}
                    stroke={isBackEdge ? '#b45309' : '#475569'}
                    strokeDasharray={isBackEdge ? '6 4' : undefined}
                    x1={points[0]!.x}
                    x2={points[1]!.x}
                    y1={points[0]!.y}
                    y2={points[1]!.y}
                />
            ))}
            {layout.nodes.map((node) => {
                const selected = tableKey(node.table) === selectedTableKey;
                return (
                    <g
                        key={tableKey(node.table)}
                        aria-label={tableLabel(node.table)}
                        aria-pressed={onSelectTable === undefined ? undefined : selected}
                        data-selected={selected ? 'true' : 'false'}
                        data-testid={`table-${tableLabel(node.table)}`}
                        onClick={onSelectTable === undefined ? undefined : () => onSelectTable(node.table)}
                        onKeyDown={
                            onSelectTable === undefined
                                ? undefined
                                : (event: KeyboardEvent<SVGGElement>) => selectByKeyboard(event, node.table, onSelectTable)
                        }
                        role={onSelectTable === undefined ? undefined : 'button'}
                        tabIndex={onSelectTable === undefined ? undefined : 0}
                    >
                        <rect
                            fill={selected ? '#dbeafe' : '#ffffff'}
                            height={node.height}
                            stroke={selected ? '#2563eb' : '#475569'}
                            strokeWidth={selected ? 3 : 1}
                            width={node.width}
                            x={node.x}
                            y={node.y}
                        />
                        <text x={node.x + 12} y={node.y + 26}>{node.table.schema}</text>
                        <text x={node.x + 12} y={node.y + 48}>{node.table.name}</text>
                    </g>
                );
            })}
        </svg>
    );
}

function selectByKeyboard(
    event: KeyboardEvent<SVGGElement>,
    table: SchemaTableAddress,
    onSelectTable: (table: SchemaTableAddress) => void,
) {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    onSelectTable(table);
}

function summary(graph: SchemaGraphProjection): string {
    return `Tables: ${graph.tables.map(tableLabel).join(', ')}. Relationships: ${graph.edges.map((edge) => `${tableLabel(edge.child)} references ${tableLabel(edge.parent)} (${edge.foreignKeyName})`).join(', ')}.`;
}

function tableKey(table: SchemaTableAddress): string {
    return `${table.schema}\u0000${table.name}`;
}

function tableLabel(table: SchemaTableAddress): string {
    return `${table.schema}.${table.name}`;
}
