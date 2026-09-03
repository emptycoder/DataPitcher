import { afterEach, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import {
  draftActions,
  useDraftDirty,
  useDraftMode,
  useDraftSelectionName,
  useDraftTab,
  usePendingVisualConfirmation,
} from './selectionDraftStore';
import type { VisualSelection } from './selectionAst';

function selection(alias = 'o'): VisualSelection {
  return { root: { tableId: 'sales.orders', alias, stableKey: ['id'] }, joins: [], predicate: null };
}

function Probe() {
  return <output>{`${useDraftMode()}|${useDraftTab()}|${useDraftDirty()}|${useDraftSelectionName()}|${usePendingVisualConfirmation()}`}</output>;
}

afterEach(() => {
  cleanup();
  draftActions.clear();
});

it('preserves a raw draft until explicit discard confirmation', () => {
  draftActions.begin(selection());
  draftActions.setSqlSnapshot('SELECT DISTINCT "o"."id" FROM "sales"."orders" AS "o"');
  draftActions.editRawSql('SELECT DISTINCT "o"."id" FROM "sales"."orders" AS "o" WHERE "o"."id" = @p0');
  render(<Probe />);

  expect(screen.getByRole('status')).toHaveTextContent('raw|visual|true||false');
  act(() => {
    draftActions.requestVisualMode();
  });
  expect(draftActions.snapshot().pendingVisualConfirmation).toBe(true);
  expect(draftActions.snapshot().rawSql).toContain('@p0');
  act(() => {
    draftActions.confirmDiscardRawSql();
  });
  expect(draftActions.snapshot().mode).toBe('visual');
  expect(screen.getByRole('status')).toHaveTextContent('visual|visual|true||false');
  expect(draftActions.snapshot().rawSql).toBeNull();
});

it('keeps raw SQL when the visual-mode confirmation is cancelled', () => {
  draftActions.begin(selection());
  draftActions.setSqlSnapshot('snapshot');
  draftActions.editRawSql('edited');
  draftActions.requestVisualMode();
  draftActions.cancelVisualMode();

  expect(draftActions.snapshot()).toMatchObject({ mode: 'raw', rawSql: 'edited', pendingVisualConfirmation: false });
});

it('retains primitive draft state across navigation and edits visual state after confirmation', () => {
  draftActions.begin(selection());
  draftActions.setSelectionName('Orders to move');
  draftActions.setTab('preview');
  const view = render(<Probe />);

  expect(screen.getByRole('status')).toHaveTextContent('visual|preview|true|Orders to move|false');
  view.unmount();
  render(<Probe />);
  expect(screen.getByRole('status')).toHaveTextContent('visual|preview|true|Orders to move|false');
  draftActions.editVisual(selection('orders'));
  expect(draftActions.snapshot().visual).toMatchObject({ root: { alias: 'orders' } });
  expect(draftActions.snapshot().lastVisualAst).toMatchObject({ root: { alias: 'orders' } });
});

it('does not silently convert raw SQL or accept edits outside their mode', () => {
  draftActions.begin(selection());
  draftActions.requestVisualMode();
  expect(draftActions.snapshot().pendingVisualConfirmation).toBe(false);
  draftActions.editRawSql('ignored without a snapshot');
  expect(draftActions.snapshot().mode).toBe('visual');
  expect(draftActions.snapshot().rawSql).toBeNull();

  draftActions.setSqlSnapshot('snapshot');
  draftActions.editRawSql('edited');
  draftActions.editVisual(selection('ignored'));
  draftActions.requestVisualMode();
  draftActions.confirmDiscardRawSql();
  expect(draftActions.snapshot().visual).toMatchObject({ root: { alias: 'o' } });
  expect(draftActions.snapshot().mode).toBe('visual');
});
