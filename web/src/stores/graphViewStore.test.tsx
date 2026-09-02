import { afterEach, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import {
  graphViewActions,
  useGraphFocusedTableId,
  useGraphViewport,
  useIsComponentCollapsed,
  useIsGraphTableExpanded,
  useIsGraphTableSelected,
  useIsSchemaCollapsed,
  usePinnedGraphPosition,
} from './graphViewStore';

function GraphViewProbe() {
  const viewport = useGraphViewport();
  const focusedTableId = useGraphFocusedTableId();
  const selected = useIsGraphTableSelected('orders');
  const expanded = useIsGraphTableExpanded('orders');
  const schemaCollapsed = useIsSchemaCollapsed('sales');
  const componentCollapsed = useIsComponentCollapsed('orders-customers');
  const pinned = usePinnedGraphPosition('orders');
  return <output role="status">{`${viewport.x}|${viewport.y}|${viewport.zoom}|${focusedTableId}|${selected}|${expanded}|${schemaCollapsed}|${componentCollapsed}|${pinned?.x}|${pinned?.y}`}</output>;
}

afterEach(() => {
  cleanup();
  graphViewActions.setViewport({ x: 0, y: 0, zoom: 1 });
  graphViewActions.setFocusedTableId(null);
  graphViewActions.setGraphTableSelected('orders', false);
  graphViewActions.setGraphTableExpanded('orders', false);
  graphViewActions.setSchemaCollapsed('sales', false);
  graphViewActions.setComponentCollapsed('orders-customers', false);
  graphViewActions.clearPinnedGraphPosition('orders');
});

it('exposes primitive graph-view selectors and updates named interaction actions', () => {
  render(<GraphViewProbe />);
  expect(screen.getByRole('status')).toHaveTextContent('0|0|1|null|false|false|false|false|undefined|undefined');

  act(() => {
    graphViewActions.setViewport({ x: 10, y: 20, zoom: 2 });
    graphViewActions.setFocusedTableId('orders');
    graphViewActions.setGraphTableSelected('orders', true);
    graphViewActions.setGraphTableExpanded('orders', true);
    graphViewActions.setSchemaCollapsed('sales', true);
    graphViewActions.setComponentCollapsed('orders-customers', true);
    graphViewActions.setPinnedGraphPosition('orders', { x: 300, y: 400 });
  });

  expect(screen.getByRole('status')).toHaveTextContent('10|20|2|orders|true|true|true|true|300|400');
});

it('removes selected, expanded, collapsed, and pinned overrides through named actions', () => {
  graphViewActions.setGraphTableSelected('orders', true);
  graphViewActions.setGraphTableExpanded('orders', true);
  graphViewActions.setSchemaCollapsed('sales', true);
  graphViewActions.setComponentCollapsed('orders-customers', true);
  graphViewActions.setPinnedGraphPosition('orders', { x: 300, y: 400 });
  render(<GraphViewProbe />);

  act(() => {
    graphViewActions.setGraphTableSelected('orders', false);
    graphViewActions.setGraphTableExpanded('orders', false);
    graphViewActions.setSchemaCollapsed('sales', false);
    graphViewActions.setComponentCollapsed('orders-customers', false);
    graphViewActions.clearPinnedGraphPosition('orders');
  });

  expect(screen.getByRole('status')).toHaveTextContent('0|0|1|null|false|false|false|false|undefined|undefined');
});
