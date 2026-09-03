export type SchemaTableAddress = Readonly<{ schema: string; name: string }>;

export type SchemaGraphEdge = Readonly<{
    child: SchemaTableAddress;
    parent: SchemaTableAddress;
    foreignKeyName: string;
}>;

export type SchemaGraphProjection = Readonly<{
    tables: readonly SchemaTableAddress[];
    edges: readonly SchemaGraphEdge[];
}>;

export type GraphLayoutPoint = Readonly<{ x: number; y: number }>;

export type GraphLayoutNode = Readonly<{
    table: SchemaTableAddress;
    layer: number;
    x: number;
    y: number;
    width: number;
    height: number;
}>;

export type GraphLayoutEdge = Readonly<{
    edge: SchemaGraphEdge;
    points: readonly GraphLayoutPoint[];
    isBackEdge: boolean;
}>;

export type GraphLayoutBounds = Readonly<{ x: number; y: number; width: number; height: number }>;

export type GraphLayout = Readonly<{
    nodes: readonly GraphLayoutNode[];
    edges: readonly GraphLayoutEdge[];
    bounds: GraphLayoutBounds;
}>;

const nodeWidth = 160;
const nodeHeight = 64;
const layerGap = 80;
const nodeGap = 24;

export function layoutSchemaGraph(graph: SchemaGraphProjection): GraphLayout {
    const tables = graph.tables.toSorted(compareTables);
    const edges = graph.edges.toSorted((left, right) => compareText(edgeKey(left), edgeKey(right)));
    const outgoing = new Map<string, SchemaGraphEdge[]>(tables.map((table) => [tableKey(table), []]));
    const layers = new Map<string, number>();
    const visiting = new Set<string>();
    const backEdges = new Set<SchemaGraphEdge>();

    for (const edge of edges) {
        outgoing.get(tableKey(edge.child))!.push(edge);
    }

    const layerFor = (table: SchemaTableAddress): number => {
        const key = tableKey(table);
        const existing = layers.get(key);
        if (existing !== undefined) return existing;

        visiting.add(key);
        const layer = Math.max(
            0,
            ...outgoing.get(key)!.map((edge) => {
                const parent = tableKey(edge.parent);
                if (visiting.has(parent)) {
                    backEdges.add(edge);
                    return 0;
                }
                return layerFor(edge.parent) + 1;
            }),
        );
        visiting.delete(key);
        layers.set(key, layer);
        return layer;
    };

    for (const table of tables) layerFor(table);

    const tablesByLayer = new Map<number, SchemaTableAddress[]>();
    for (const table of tables) {
        const layer = layers.get(tableKey(table))!;
        const inLayer = tablesByLayer.get(layer);
        if (inLayer === undefined) tablesByLayer.set(layer, [table]);
        else inLayer.push(table);
    }

    const positions = new Map<string, GraphLayoutNode>();
    const nodes: GraphLayoutNode[] = [];
    for (const [layer, layerTables] of [...tablesByLayer].toSorted(([left], [right]) => left - right)) {
        for (const [index, table] of layerTables.entries()) {
            const node = {
                table,
                layer,
                x: layer * (nodeWidth + layerGap),
                y: index * (nodeHeight + nodeGap),
                width: nodeWidth,
                height: nodeHeight,
            };
            positions.set(tableKey(table), node);
            nodes.push(node);
        }
    }

    return {
        nodes,
        edges: edges.map((edge) => layoutEdge(edge, positions, backEdges.has(edge))),
        bounds: {
            x: 0,
            y: 0,
            width: Math.max(0, ...nodes.map((node) => node.x + node.width)),
            height: Math.max(0, ...nodes.map((node) => node.y + node.height)),
        },
    };
}

function layoutEdge(
    edge: SchemaGraphEdge,
    positions: Map<string, GraphLayoutNode>,
    isBackEdge: boolean,
): GraphLayoutEdge {
    const child = positions.get(tableKey(edge.child))!;
    const parent = positions.get(tableKey(edge.parent))!;
    const childIsRightOfParent = child.x > parent.x;
    const childCenter = child.y + child.height / 2;
    const parentCenter = parent.y + parent.height / 2;

    return {
        edge,
        isBackEdge,
        points: childIsRightOfParent
            ? [
                  { x: child.x, y: childCenter },
                  { x: parent.x + parent.width, y: parentCenter },
              ]
            : [
                  { x: child.x + child.width, y: childCenter },
                  { x: parent.x, y: parentCenter },
              ],
    };
}

function tableKey(table: SchemaTableAddress): string {
    return `${table.schema}\u0000${table.name}`;
}

function edgeKey(edge: SchemaGraphEdge): string {
    return `${tableKey(edge.child)}\u0000${tableKey(edge.parent)}\u0000${edge.foreignKeyName}`;
}

function compareTables(left: SchemaTableAddress, right: SchemaTableAddress): number {
    return compareText(tableKey(left), tableKey(right));
}

function compareText(left: string, right: string): number {
    return Number(left > right) - Number(left < right);
}
