import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type WheelEvent } from 'react';
import { layoutSchemaGraph, type SchemaGraphProjection, type SchemaTableAddress } from '../graph/graphLayout';
import { Button, cx } from '../../ui';
import { Icons } from '../../ui/icons';

export type NodeTone = 'default' | 'root' | 'dependency' | 'cycle' | 'muted' | 'blocked';

export type SchemaGraphProps = Readonly<{
  graph: SchemaGraphProjection;
  selectedKey?: string | null;
  onSelect?: (table: SchemaTableAddress) => void;
  toneFor?: (table: SchemaTableAddress) => NodeTone;
  className?: string;
  height?: number;
}>;

export function tableKey(table: SchemaTableAddress) {
  return `${table.schema}.${table.name}`;
}

const toneStyles: Record<NodeTone, { fill: string; stroke: string; text: string }> = {
  default: { fill: 'var(--surface)', stroke: 'var(--border-strong)', text: 'var(--fg)' },
  root: { fill: 'var(--accent-soft)', stroke: 'var(--accent)', text: 'var(--fg)' },
  dependency: { fill: 'var(--info-soft)', stroke: 'var(--info)', text: 'var(--fg)' },
  cycle: { fill: 'var(--warning-soft)', stroke: 'var(--warning)', text: 'var(--fg)' },
  blocked: { fill: 'var(--danger-soft)', stroke: 'var(--danger)', text: 'var(--fg)' },
  muted: { fill: 'var(--surface-2)', stroke: 'var(--border)', text: 'var(--fg-faint)' },
};

export function SchemaGraph({ graph, selectedKey, onSelect, toneFor, className, height = 520 }: SchemaGraphProps) {
  const layout = useMemo(() => layoutSchemaGraph(graph), [graph]);
  const container = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ x: 24, y: 24, scale: 1 });
  const drag = useRef<{ startX: number; startY: number; originX: number; originY: number; moved: boolean } | null>(null);

  const fit = useCallback(() => {
    const element = container.current;
    if (!element) return;
    const width = element.clientWidth;
    const scale = Math.min(1.25, Math.max(0.15, Math.min((width - 48) / Math.max(1, layout.bounds.width), (height - 48) / Math.max(1, layout.bounds.height))));
    setView({ x: (width - layout.bounds.width * scale) / 2, y: (height - layout.bounds.height * scale) / 2, scale });
  }, [layout, height]);

  useEffect(() => {
    fit();
  }, [fit]);

  const neighbors = useMemo(() => {
    const set = new Set<string>();
    if (!selectedKey) return set;
    for (const edge of graph.edges) {
      if (tableKey(edge.child) === selectedKey) set.add(tableKey(edge.parent));
      if (tableKey(edge.parent) === selectedKey) set.add(tableKey(edge.child));
    }
    return set;
  }, [graph, selectedKey]);

  function onWheel(event: WheelEvent<HTMLDivElement>) {
    event.preventDefault();
    const rect = container.current!.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const py = event.clientY - rect.top;
    setView((current) => {
      const factor = Math.exp(-event.deltaY * 0.0015);
      const scale = Math.min(3, Math.max(0.1, current.scale * factor));
      const ratio = scale / current.scale;
      return { scale, x: px - (px - current.x) * ratio, y: py - (py - current.y) * ratio };
    });
  }

  function onPointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    drag.current = { startX: event.clientX, startY: event.clientY, originX: view.x, originY: view.y, moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function onPointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const state = drag.current;
    if (!state) return;
    const dx = event.clientX - state.startX;
    const dy = event.clientY - state.startY;
    if (Math.abs(dx) + Math.abs(dy) > 3) state.moved = true;
    setView((current) => ({ ...current, x: state.originX + dx, y: state.originY + dy }));
  }
  function onPointerUp() {
    drag.current = null;
  }

  if (layout.nodes.length === 0) {
    return <div className={cx('flex items-center justify-center rounded-xl border border-dashed border-border text-sm text-fg-muted', className)} style={{ height }}>No tables to display.</div>;
  }

  return (
    <div className={cx('relative overflow-hidden rounded-xl border border-border bg-surface-2', className)} style={{ height }}>
      <div
        className="size-full cursor-grab touch-none select-none active:cursor-grabbing"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onWheel={onWheel}
        ref={container}
        role="presentation"
      >
        <svg aria-label="Schema dependency graph" className="size-full" role="img">
          <defs>
            <marker id="dp-arrow" markerHeight="8" markerWidth="8" orient="auto" refX="7" refY="4">
              <path d="M 0 0 L 8 4 L 0 8 z" fill="var(--fg-faint)" />
            </marker>
            <marker id="dp-arrow-active" markerHeight="8" markerWidth="8" orient="auto" refX="7" refY="4">
              <path d="M 0 0 L 8 4 L 0 8 z" fill="var(--accent)" />
            </marker>
            <marker id="dp-arrow-back" markerHeight="8" markerWidth="8" orient="auto" refX="7" refY="4">
              <path d="M 0 0 L 8 4 L 0 8 z" fill="var(--warning)" />
            </marker>
            <pattern height="24" id="dp-grid" patternUnits="userSpaceOnUse" width="24">
              <circle cx="1" cy="1" fill="var(--border)" r="1" />
            </pattern>
          </defs>
          <rect fill="url(#dp-grid)" height="100%" width="100%" />
          <g transform={`translate(${view.x} ${view.y}) scale(${view.scale})`}>
            {layout.edges.map(({ edge, points, isBackEdge }) => {
              const childKey = tableKey(edge.child);
              const parentKey = tableKey(edge.parent);
              const touching = selectedKey !== null && selectedKey !== undefined && (childKey === selectedKey || parentKey === selectedKey);
              const dimmed = Boolean(selectedKey) && !touching;
              const [from, to] = [points[0]!, points[1]!];
              const dx = Math.max(40, Math.abs(to.x - from.x) / 2);
              const path = `M ${from.x} ${from.y} C ${from.x - dx} ${from.y}, ${to.x + dx} ${to.y}, ${to.x} ${to.y}`;
              return (
                <path
                  d={path}
                  fill="none"
                  key={`${childKey}->${parentKey}:${edge.foreignKeyName}`}
                  markerEnd={`url(#${isBackEdge ? 'dp-arrow-back' : touching ? 'dp-arrow-active' : 'dp-arrow'})`}
                  opacity={dimmed ? 0.18 : 1}
                  stroke={isBackEdge ? 'var(--warning)' : touching ? 'var(--accent)' : 'var(--fg-faint)'}
                  strokeDasharray={isBackEdge ? '6 4' : undefined}
                  strokeWidth={touching ? 2 : 1.25}
                >
                  <title>{`${childKey} → ${parentKey} (${edge.foreignKeyName})`}</title>
                </path>
              );
            })}
            {layout.nodes.map((node) => {
              const key = tableKey(node.table);
              const selected = key === selectedKey;
              const tone = toneFor?.(node.table) ?? 'default';
              const style = toneStyles[tone];
              const dimmed = Boolean(selectedKey) && !selected && !neighbors.has(key) && tone !== 'root';
              return (
                <g
                  className={onSelect ? 'cursor-pointer' : undefined}
                  key={key}
                  onClick={() => {
                    if (drag.current?.moved) return;
                    onSelect?.(node.table);
                  }}
                  opacity={dimmed ? 0.35 : 1}
                  role={onSelect ? 'button' : undefined}
                  transform={`translate(${node.x} ${node.y})`}
                >
                  <rect
                    fill={style.fill}
                    height={node.height}
                    rx={10}
                    stroke={selected ? 'var(--accent)' : style.stroke}
                    strokeWidth={selected ? 2.5 : 1.25}
                    width={node.width}
                  />
                  <text fill="var(--fg-faint)" fontFamily="var(--font-mono)" fontSize="10" x={12} y={22}>
                    {node.table.schema}
                  </text>
                  <text fill={style.text} fontSize="13" fontWeight={600} x={12} y={44}>
                    {node.table.name.length > 18 ? `${node.table.name.slice(0, 17)}…` : node.table.name}
                  </text>
                  <title>{key}</title>
                </g>
              );
            })}
          </g>
        </svg>
      </div>
      <div className="absolute right-3 bottom-3 flex gap-1 rounded-lg border border-border bg-surface p-1 shadow-card">
        <Button onClick={() => setView((current) => ({ ...current, scale: Math.min(3, current.scale * 1.25) }))} size="sm" variant="ghost">
          <Icons.Plus size={14} />
        </Button>
        <Button onClick={() => setView((current) => ({ ...current, scale: Math.max(0.1, current.scale / 1.25) }))} size="sm" variant="ghost">
          −
        </Button>
        <Button onClick={fit} size="sm" variant="ghost">
          Fit
        </Button>
      </div>
      <div className="absolute top-3 left-3 rounded-lg border border-border bg-surface/90 px-2.5 py-1.5 text-[11px] text-fg-muted backdrop-blur">
        {layout.nodes.length} tables · {layout.edges.length} foreign keys
        {layout.edges.some((edge) => edge.isBackEdge) ? <span className="ml-2 text-warning">· dashed = cycle edge</span> : null}
      </div>
    </div>
  );
}
