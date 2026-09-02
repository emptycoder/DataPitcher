import { useEffect, useRef, type KeyboardEvent } from 'react';
import type { GraphTable } from './model';
import { presentGraphState } from './presentation';

export type GraphNodeData = Readonly<{
  itemId: string;
  table: GraphTable;
  selected: boolean;
  focused: boolean;
  parentItemId: string | null;
  dependantItemId: string | null;
  firstItemId: string;
  lastItemId: string;
  onSelect: (itemId: string) => void;
  onFocus: (itemId: string) => void;
}>;

export function GraphNode({ data }: Readonly<{ data: GraphNodeData }>) {
  const button = useRef<HTMLButtonElement>(null);
  const state = presentGraphState(data.table.state);

  useEffect(() => {
    if (data.focused) button.current?.focus();
  }, [data.focused]);

  function handleKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      data.onSelect(data.itemId);
      return;
    }
    const focusTarget = {
      ArrowRight: data.parentItemId,
      ArrowLeft: data.dependantItemId,
      Home: data.firstItemId,
      End: data.lastItemId,
    }[event.key];
    if (focusTarget) {
      event.preventDefault();
      data.onFocus(focusTarget);
    }
  }

  return (
    <button
      ref={button}
      type="button"
      aria-pressed={data.selected}
      className={`border ${state.borderClass} p-2 text-left`}
      onClick={() => data.onSelect(data.itemId)}
      onKeyDown={handleKeyDown}
    >
      <span aria-hidden="true">{state.icon}</span>
      <span> {state.label}</span>
      <span> {data.table.schema}.{data.table.name}</span>
    </button>
  );
}
