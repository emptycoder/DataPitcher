# DataPitcher Slice 19: Frontend Selection Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a fully covered desktop Selection Workbench in which an operator builds, previews, counts, saves, and safely revisits an exact root-key selection without client-generated SQL.

**Architecture:** TanStack Query owns refetchable schema, saved-selection, SQL-snapshot, preview, and count data; an in-memory Zustand draft store owns only the current selection’s small client interaction state. Pure TypeScript modules define and edit the visual AST, transition visual and raw modes, map request outcomes, and derive rendering rows; React components render accessible controls, while Monaco, the virtualizer, fetch, authentication, clock, and scheduler remain injected adapter seams. The browser sends a structured AST and typed parameter values to the API; provider code validates and generates SQL, so the browser never concatenates SQL.

**Tech Stack:** Node 22.22.2+, npm with committed lockfile, React 19.2.8, TypeScript 6.0.3 strict mode, Vite 8.2.2, Zustand 5.0.15, TanStack React Query 5.102.8, `@tanstack/react-virtual` 3.13.18, Monaco Editor 0.55.1, Zod 4.5.4, Orval 8.27.0, Vitest 4.1.11 with happy-dom 20.13.1 and V8 coverage, React Testing Library 16.3.3, and Playwright 1.62.1.

---

## File Structure

- `web/package.json`, `web/package-lock.json` — exact virtualizer and Monaco pins and the resulting npm graph.
- `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts` — one selection-workbench transport contract and committed generated client/schema artifacts.
- `web/src/features/selections/selectionAst.ts` — browser-side discriminated AST, typed values, column operator policy, and pure AST edits.
- `web/src/features/selections/selectionDraftStore.ts` — non-persisted draft, visual/raw transition policy, tab, dirty state, and narrow primitive selectors.
- `web/src/features/selections/workbenchPreferences.ts` — injected-storage favourites and recents preferences with a persistence allowlist.
- `web/src/features/selections/workbenchApi.ts`, `web/src/features/selections/workbenchQueries.ts` — authenticated injected-fetch boundary and Query option factories.
- `web/src/features/selections/requestState.ts` — pure loading, empty, error, forbidden, cancelled, stale, and token-expired presentation policy.
- `web/src/features/selections/SchemaBrowser.tsx`, `SelectionWorkbench.tsx`, `VisualBuilder.tsx`, `SqlTab.tsx`, `PreviewTab.tsx`, `SelectionCart.tsx` — rendering-only workbench regions and tabs.
- `web/src/features/selections/monacoAdapter.ts`, `virtualGrid.ts` — thin Monaco and TanStack Virtual adapters.
- `web/src/features/selections/*.test.ts`, `*.test.tsx` — same-task unit and component coverage for every handwritten feature module.
- `web/src/app/App.tsx`, `web/src/app/App.test.tsx` — workbench mount and the in-memory navigation boundary.
- `web/e2e/selection-workbench.spec.ts` — one real-browser Monaco mount/disposal smoke test, separate from Vitest coverage.

## Scope and Deferrals

This is the Selection Workbench only. It deliberately excludes the dependency graph implementation, plan review/sealing UI, and transfer monitor/SSE UI; navigation may leave the workbench and return, but this slice does not create those destination screens or their server-state logic. The draft provider remains mounted above feature navigation, so an operator can open the eventual graph route and return without losing a visual AST, raw SQL, active tab, or dirty indication. Refreshing the browser is not a promised recovery mechanism: selections and raw parameter values are deliberately not persisted.

The workbench consumes the selection API delivered by the selections runtime slice. Its checked-in OpenAPI contract adds schema browsing, saved selections, compilation, preview, and count operations so Orval has one source for URL helpers, types, and Zod schemas; it does not create a browser-side fake database or duplicate the C# AST. Each request carries either a structured visual AST or permission-gated raw SQL and typed values. The server independently validates permissions, schema revision, AST types, raw SQL’s read-only single-statement rule, stable-key projection, preview cap, and cancellation. A 401, 403, 409/412 stale response, or problem payload never becomes optimistic UI truth.

The SQL tab has an explicit irreversible transition. A visual draft requests a server-generated SQL snapshot. If a permitted operator changes that snapshot, `editRawSql` changes mode to `raw` and retains `lastVisualAst`; it does not attempt to parse SQL into the AST. Selecting Visual Builder from raw mode opens a confirmation dialog naming the raw SQL that will be discarded. Only `confirmDiscardRawSql` restores `lastVisualAst`; cancel keeps raw mode unchanged. Operators without `Selections.RawSql` can inspect the generated read-only snapshot but cannot focus an editable Monaco model or invoke a raw request.

Preview and count are different actions. Preview shows bounded server-returned rows in a virtualized grid. Count is an explicit button, never an AST change effect, and displays “distinct stable keys” beside the number: five joined order-line rows still count as one order if the order stable key is the root. The right rail never presents preview-row count as transfer size. Values remain typed data in the AST/request body; neither AST code nor Monaco code interpolates SQL text.

### Task 1: Define the client AST and pure visual edits

**Files:**
- Create: `web/src/features/selections/selectionAst.ts`, `web/src/features/selections/selectionAst.test.ts`
- Modify: none
- Test: `web/src/features/selections/selectionAst.test.ts`

1. - [ ] **Write the failing AST test with every editor operator.** Create this complete test; it imports the absent module and exercises nesting, negation, typed values, joins, and the operator menu rather than a rendered implementation detail.

   ```ts
   import { expect, it } from 'vitest';
   import { addJoin, operatorsFor, replacePredicate, type VisualSelection } from './selectionAst';

   const selection: VisualSelection = { root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id'] }, joins: [], predicate: null };
   it('edits a typed nested AST without SQL text', () => {
     const nested = { kind: 'not' as const, term: { kind: 'or' as const, terms: [
       { kind: 'between' as const, column: { alias: 'o', name: 'created', valueKind: 'date' as const }, lower: { kind: 'date' as const, value: '2026-09-01' }, upper: { kind: 'date' as const, value: '2026-09-02' } },
       { kind: 'exists' as const, tableId: 'sales.lines', alias: 'l', correlations: [{ outer: { alias: 'o', name: 'id', valueKind: 'int' as const }, innerColumn: 'order_id' }], predicate: { kind: 'text' as const, match: 'contains' as const, column: { alias: 'l', name: 'sku', valueKind: 'string' as const }, value: { kind: 'string' as const, value: 'A' } }, negated: false },
     ] } };
     expect(replacePredicate(selection, nested).predicate).toEqual(nested);
     expect(addJoin(selection, { kind: 'foreignKey', fromAlias: 'o', alias: 'c', foreignKeyId: 'fk_orders_customer', direction: 'forward' }).joins).toHaveLength(1);
     expect(operatorsFor('string')).toEqual(['equal', 'notEqual', 'in', 'isNull', 'isNotNull', 'contains', 'startsWith', 'endsWith']);
     expect(operatorsFor('date')).toEqual(['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateRange']);
   });
    ```

   Keep each constrained operator literal narrow in the fixture (for example, `match: 'contains' as const`), rather than widening the AST union. This preserves the union that prevents invalid operators from reaching server SQL generation.

2. - [ ] **Run the focused test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/selectionAst.test.ts`; expect non-zero exit and `Failed to resolve import "./selectionAst"`.

3. - [ ] **Implement the complete browser AST vocabulary and immutable edits.** Create `selectionAst.ts` with `ValueKind` of `int | decimal | string | boolean | date | time | dateTime | guid`, `TypedValue` as `{ kind: ValueKind; value: string | number | boolean }`, and `ColumnRef` as `{ alias; name; valueKind }`. Define `Predicate` as the discriminated union `and`, `or`, `not`, `comparison`, `between`, `set`, `null`, `text`, `boolean`, `temporalRange`, and `exists`; `Join` as the `foreignKey` and `manual` union; and `VisualSelection` as in the test. `comparison` uses `equal | notEqual | greaterThan | greaterOrEqual | lessThan | lessOrEqual`; `set` carries `negated`; `null` carries `negated`; `text` carries `contains | startsWith | endsWith`; and `temporalRange` carries `date | time | dateTime`. Export the following complete edit functions and an operator table, using no SQL strings:

   ```ts
   export function replacePredicate(selection: VisualSelection, predicate: Predicate | null): VisualSelection {
     return { ...selection, predicate };
   }
   export function addJoin(selection: VisualSelection, join: Join): VisualSelection {
     return { ...selection, joins: [...selection.joins, join] };
   }
   export function selectionFingerprint(selection: VisualSelection): string { return JSON.stringify(selection); }
   const operators: Record<ValueKind, readonly Operator[]> = {
     int: ['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull'],
     decimal: ['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull'],
     string: ['equal', 'notEqual', 'in', 'isNull', 'isNotNull', 'contains', 'startsWith', 'endsWith'],
     boolean: ['equal', 'notEqual', 'isNull', 'isNotNull'],
     date: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateRange'],
     time: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'timeRange'],
     dateTime: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateTimeRange'],
     guid: ['equal', 'notEqual', 'in', 'isNull', 'isNotNull'],
   };
   export function operatorsFor(valueKind: ValueKind): readonly Operator[] { return operators[valueKind]; }
   ```

   Keep `validateVisualSelection(selection, schema)` pure: it checks root stable-key order, aliases, column/value-kind equality, two-or-more group terms, non-empty sets, typed range bounds, known forward/reverse foreign-key IDs, and same-kind manual pairs. It returns an immutable string array for inline rendering; it does not produce, parse, or repair SQL. Add table-driven assertions in the test for every remaining value kind, predicate union member, validation failure, forward/reverse join, manual join, and `selectionFingerprint` so `selectionAst.ts` reaches all four 100% counters in this task.

4. - [ ] **Run the AST unit test and confirm it passes.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/selections/selectionAst.test.ts`; expect exit 0 and the AST, operator, and validation assertions to pass.

5. - [ ] **Commit the pure AST boundary.** Run `git add web/src/features/selections/selectionAst.ts web/src/features/selections/selectionAst.test.ts && git commit -m "feat: add selection workbench AST"`.

### Task 2: Keep an in-progress draft through navigation and raw-mode transitions

**Files:**
- Create: `web/src/features/selections/selectionDraftStore.ts`, `web/src/features/selections/selectionDraftStore.test.tsx`
- Modify: none
- Test: `web/src/features/selections/selectionDraftStore.test.tsx`

1. - [ ] **Write the failing draft-transition test.** Create this complete test, which deliberately observes primitive selectors and requires confirmation before raw SQL is lost.

   ```tsx
   import { expect, it } from 'vitest';
   import { render, screen } from '@testing-library/react';
   import { draftActions, useDraftMode, useDraftDirty, useDraftTab } from './selectionDraftStore';

   function Probe() { return <output role="status">{`${useDraftMode()}|${useDraftTab()}|${useDraftDirty()}`}</output>; }
   it('preserves a raw draft until explicit discard confirmation', () => {
     draftActions.begin({ root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id'] }, joins: [], predicate: null });
     draftActions.setSqlSnapshot('SELECT DISTINCT "o"."id" FROM "sales"."orders" AS "o"');
     draftActions.editRawSql('SELECT DISTINCT "o"."id" FROM "sales"."orders" AS "o" WHERE "o"."id" = @p0');
     render(<Probe />);
     expect(screen.getByRole('status')).toHaveTextContent('raw|visual|true');
     draftActions.requestVisualMode();
     expect(draftActions.snapshot().pendingVisualConfirmation).toBe(true);
     expect(draftActions.snapshot().rawSql).toContain('@p0');
     draftActions.confirmDiscardRawSql();
     expect(screen.getByRole('status')).toHaveTextContent('visual|visual|true');
   });
   ```

2. - [ ] **Run the focused transition test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/selectionDraftStore.test.tsx`; expect non-zero exit and `Failed to resolve import "./selectionDraftStore"`.

3. - [ ] **Implement the non-persisted draft store and explicit transition machine.** Define `DraftMode = 'visual' | 'raw'`, `WorkbenchTab = 'visual' | 'sql' | 'preview' | 'explain'`, and `SelectionDraft` containing `selectionName`, `visual`, `lastVisualAst`, `sqlSnapshot`, `rawSql`, `mode`, `tab`, `dirty`, and `pendingVisualConfirmation`. Create a private Zustand hook and export only `draftActions`, `useDraftMode`, `useDraftTab`, `useDraftDirty`, `useDraftSelectionName`, and `usePendingVisualConfirmation`; every selector returns a primitive. `begin(visual)` resets a new draft. `setSelectionName(name)` marks it dirty. `editVisual` replaces both `visual` and `lastVisualAst`, marks dirty, and is legal only in visual mode. `setSqlSnapshot` records the server result without changing mode. `editRawSql` requires a non-empty snapshot, stores the edited text, sets `mode: 'raw'`, and marks dirty. `requestVisualMode` sets only `pendingVisualConfirmation` when mode is raw; it switches immediately only if already visual. `cancelVisualMode` clears only the pending flag. `confirmDiscardRawSql` restores `lastVisualAst`, clears `rawSql`, clears the pending flag, and sets visual mode. `setTab` changes only the tab. `snapshot` reads private state for tests; it does not expose the Zustand hook to components.

   The store has no persistence middleware, transport import, access token, server payload, `localStorage`, or generic patch method. It is mounted by import at application scope, not inside a tab, so unmounting a future graph route cannot erase the draft. Add assertions for cancelling the dialog, changing tabs and returning, editing visual after confirmation, and attempting no automatic raw-to-visual conversion; reset with `draftActions.clear()` after each test.

4. - [ ] **Run the transition test and confirm it passes.** Run `npm --prefix web test -- --run src/features/selections/selectionDraftStore.test.tsx`; expect exit 0 with raw preservation, explicit discard, primitive selection, navigation persistence, and reset assertions passing.

5. - [ ] **Commit the draft ownership boundary.** Run `git add web/src/features/selections/selectionDraftStore.ts web/src/features/selections/selectionDraftStore.test.tsx && git commit -m "feat: retain selection draft state"`.

### Task 3: Render the desktop shell and schema browser preferences

**Files:**
- Create: `web/src/features/selections/workbenchPreferences.ts`, `web/src/features/selections/SchemaBrowser.tsx`, `web/src/features/selections/SelectionWorkbench.tsx`, `web/src/features/selections/SchemaBrowser.test.tsx`
- Modify: none
- Test: `web/src/features/selections/SchemaBrowser.test.tsx`

1. - [ ] **Write the failing accessible three-column test.** Render `SelectionWorkbench` with an injected `SchemaBrowser` model containing one stable-key table and one blocked table; assert landmarks named “Schema browser”, “Selection editor”, and “Selection cart”, the searchable Orders row, its approximate count, “Stable key unavailable” warning, Favourite action, and Recent indicator. Use `getByRole` and visible names only.

2. - [ ] **Run the missing-component test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/SchemaBrowser.test.tsx`; expect non-zero exit and `Failed to resolve import "./SelectionWorkbench"`.

3. - [ ] **Implement the rendering-only shell, injected favourites, and recents.** Define a `SelectionTableSummary` prop with `tableId`, `schemaName`, `tableName`, `approximateRowCount: number | null`, `stableKeyColumns: readonly string[] | null`, and `selected`. `SchemaBrowser` filters its received list by a controlled search string and exposes a labelled search box. Each result is a button with its selected state in `aria-pressed`, an approximate-count label of `≈ 12,500 rows` or `Count unavailable`, a stable-key warning when null, and named quick actions “Select root”, “Toggle favourite”, and “Show columns”. Selecting a root calls the supplied action only when the stable key exists; it never fetches or calls SQL.

   Create `createWorkbenchPreferences(storage: StateStorage)` using `persist`, `createJSONStorage(() => storage)`, an explicit `partialize` allowlist of `favouriteTableIds` and `recentTableIds`, `toggleFavourite(tableId)`, and `recordRecent(tableId)`. `recordRecent` prepends a unique identifier and retains ten entries. The production instance receives `window.localStorage`, while all tests pass an in-memory `StateStorage`; never read ambient `localStorage`. Export only named actions and primitive `useIsFavourite(tableId)` / `useIsRecent(tableId)` selectors, avoiding a freshly allocated selector object.

   `SelectionWorkbench` is a desktop CSS grid with `grid-template-columns: minmax(16rem, 22rem) minmax(32rem, 1fr) minmax(18rem, 24rem)` and its three labelled `<aside>`, `<main>`, `<aside>` regions. Its centre header shows current root table, ordered stable-key columns, editable selection name, and the four tabs; it accepts a `rightRail: ReactNode` slot so Task 8 can wire the cart without importing a file created later. It contains no dependency-graph, plan, or job markup. Extend the test to prove filtering, favourite persistence through the injected storage value, recency order, disabled root action for the blocked table, and every quick-action callback.

4. - [ ] **Run the shell test and confirm it passes.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/selections/SchemaBrowser.test.tsx`; expect exit 0 with all desktop landmarks, search, preference, warning, and quick-action assertions passing.

5. - [ ] **Commit the workbench frame.** Run `git add web/src/features/selections/workbenchPreferences.ts web/src/features/selections/SchemaBrowser.tsx web/src/features/selections/SelectionWorkbench.tsx web/src/features/selections/SchemaBrowser.test.tsx && git commit -m "feat: add selection workbench layout"`.

### Task 4: Add generated workbench transport and exhaustive request states

**Files:**
- Create: `web/src/features/selections/workbenchApi.ts`, `web/src/features/selections/workbenchQueries.ts`, `web/src/features/selections/requestState.ts`, `web/src/features/selections/workbenchApi.test.ts`
- Modify: `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts`
- Test: `web/src/features/selections/workbenchApi.test.ts`

1. - [ ] **Write the failing API/state test.** Test `toRequestState` with a pending request, empty successful list, ordinary error, `SelectionRequestError(403)`, an abort error, `SelectionRequestError(409)`, `SelectionRequestError(412)`, and `SelectionRequestError(401)`, expecting exactly `loading`, `empty`, `error`, `forbidden`, `cancelled`, `stale`, `stale`, and `tokenExpired`. Add a `fetchPreview` test whose injected request waits for and observes `signal.aborted`; call `QueryClient.cancelQueries` for its exact preview key and assert the promise rejects as cancellation.

2. - [ ] **Run the focused API/state test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/workbenchApi.test.ts`; expect non-zero exit and `Failed to resolve import "./workbenchApi"`.

3. - [ ] **Define one generated contract and implement the injected API boundary.** Extend the checked-in OpenAPI document with these exact operation IDs and response schemas: `getSelectionWorkbenchSchema`, `listSelections`, `compileSelection`, `previewSelection`, `countSelection`, and existing `saveSelection`. The schema response contains table summaries, columns with `valueKind`, stable-key columns, and known FK paths; compilation returns `sqlSnapshot`, typed parameter definitions without values, warnings, and schema revision; preview returns bounded `columns`, `rows: Record<string, unknown>[]`, `hasMore`, and revision; count returns `distinctStableKeyCount`; saved selections contain `selectionId`, `displayName`, `version`, `eTag`, `mode`, and warning summaries. Requests carry `{ mode, visual, rawSql, parameters, schemaRevision }`; raw requests are separately server-authorized. Regenerate with `npm --prefix web run generate:api`; import the actual emitted `getSelectionWorkbenchSchemaUrl`, `listSelectionsUrl`, `compileSelectionUrl`, `previewSelectionUrl`, `countSelectionUrl`, `saveSelectionUrl`, and their generated `...Response` Zod schemas rather than handwritten equivalents.

   `workbenchApi.ts` exports injected `RequestFunction`, `SelectionRequestError`, and request functions that obtain a token from `AuthenticationAdapter`, add `Authorization`, pass the Query-provided abort signal, call a generated URL helper, and parse success data through the generated Zod schema. Map statuses 401, 403, 409, and 412 to `SelectionRequestError` without exposing authorization detail; rethrow all other parsed failures. `workbenchQueries.ts` exports `previewQueryKey(draft)` and deterministic option factories keyed only on connection, snapshot, selection, `selectionFingerprint(draft.visual)`, and schema revision, with `retry: false`. No token, raw parameter value, preview dataset, or response object enters Zustand.

   Implement the complete state mapper as this pure TypeScript fragment, then render its result in every query-backed pane in later tasks:

   ```ts
   export type RequestState = 'loading' | 'ready' | 'empty' | 'error' | 'forbidden' | 'cancelled' | 'stale' | 'tokenExpired';
   export function toRequestState(input: { pending: boolean; empty: boolean; cancelled: boolean; error: unknown }): RequestState {
     if (input.pending) return 'loading';
     if (input.cancelled) return 'cancelled';
     if (input.error instanceof SelectionRequestError && input.error.status === 401) return 'tokenExpired';
     if (input.error instanceof SelectionRequestError && input.error.status === 403) return 'forbidden';
     if (input.error instanceof SelectionRequestError && (input.error.status === 409 || input.error.status === 412)) return 'stale';
     if (input.error) return 'error';
     return input.empty ? 'empty' : 'ready';
   }
   ```

   Test every API function’s generated URL, authorization header, schema rejection, 401/403/409/412 mapping, missing-token error, query key, and true abort signal propagation. Do not use `fetch` directly in a component and do not build a path from `import.meta.url`.

4. - [ ] **Generate and run the request-boundary tests.** Run `npm --prefix web run generate:api && npm --prefix web run typecheck && npm --prefix web test -- --run src/features/selections/workbenchApi.test.ts`; expect exit 0 and all seven state classifications plus the aborted preview assertion to pass.

5. - [ ] **Commit the contract and API seam.** Run `git add web/openapi/datapitcher.openapi.json web/src/api/generated/client.ts web/src/api/generated/permissions.zod.ts web/src/features/selections/workbenchApi.ts web/src/features/selections/workbenchQueries.ts web/src/features/selections/requestState.ts web/src/features/selections/workbenchApi.test.ts && git commit -m "feat: add selection workbench API boundary"`.

### Task 5: Render the visual AST builder and generated SQL snapshot

**Files:**
- Create: `web/src/features/selections/VisualBuilder.tsx`, `web/src/features/selections/VisualBuilder.test.tsx`
- Modify: `web/src/features/selections/SelectionWorkbench.tsx`
- Test: `web/src/features/selections/VisualBuilder.test.tsx`

1. - [ ] **Write the failing visual-builder component test.** Render a supplied visual AST and schema model. Assert controls by name for “Add AND group”, “Add OR group”, “Negate condition”, “Between”, “In list”, “Is null”, “Contains”, “Starts with”, “Ends with”, “Date range”, “Time range”, “Add known relationship”, “Add reverse relationship”, “Add manual join”, and “Add exists”. Click the actions and assert the supplied immutable callbacks receive AST members with typed values; assert the rendered root stable key remains `id, tenant_id` in that order.

2. - [ ] **Run the missing-builder test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/VisualBuilder.test.tsx`; expect non-zero exit and `Failed to resolve import "./VisualBuilder"`.

3. - [ ] **Implement a controlled rendering-only visual builder.** `VisualBuilder` receives `selection`, schema metadata, `validationMessages`, and `onChange`; it calls the pure functions from Task 1 and owns only transient input text. Render nested groups recursively with `<fieldset>` and `<legend>` labels, use `aria-label` to distinguish each column/operator/value control, and render `NOT` as a labelled group wrapper. Column selection filters the Task 1 operator list, so strings get text operators, date/time/date-time get temporal ranges, and values become `{ kind: column.valueKind, value }` before `onChange`. The `EXISTS` editor accepts a table, alias, one-or-more typed correlations, nested optional predicate, and negation. The join editor offers known FK forward and reverse paths from schema IDs; its manual alternative requires source alias, target table, alias, and one-or-more matching-type pairs.

   Validation messages are rendered in a named `role="alert"` list and disable only “Request SQL snapshot” while the AST is invalid. They do not disable visual editing or silently coerce input. The component has no Query call, SQL string, database terminology beyond user-visible metadata, or state-store subscription; the parent obtains a snapshot only through `compileSelection` from Task 4. Cover add/remove term, each predicate branch, nested group, not, all joins, empty validation, invalid validation, and accessible keyboard selection so every visual-builder branch is exercised in this task.

4. - [ ] **Run the builder test and confirm it passes.** Run `npm --prefix web test -- --run src/features/selections/VisualBuilder.test.tsx`; expect exit 0 with all operator, boolean, exists, join, validation, and ordered-stable-key assertions passing.

5. - [ ] **Commit the visual editor.** Run `git add web/src/features/selections/VisualBuilder.tsx web/src/features/selections/VisualBuilder.test.tsx web/src/features/selections/SelectionWorkbench.tsx && git commit -m "feat: add visual selection builder"`.

### Task 6: Gate raw SQL and isolate Monaco lifecycle effects

**Files:**
- Create: `web/src/features/selections/monacoAdapter.ts`, `web/src/features/selections/SqlTab.tsx`, `web/src/features/selections/SqlTab.test.tsx`
- Modify: `web/package.json`, `web/package-lock.json`
- Test: `web/src/features/selections/SqlTab.test.tsx`

1. - [ ] **Write the failing Monaco/permission test.** Supply a fake adapter whose model records `setValue` and disposal, whose editor records `onDidChangeModelContent`, resize/layout, and disposal. Verify a snapshot is read-only without `Selections.RawSql`, a permitted user’s edit calls `draftActions.editRawSql`, programmatic snapshot replacement does not call it, resize invokes layout, unmount disposes listener/editor/model, and the “Return to Visual Builder” button opens—not bypasses—the discard confirmation.

2. - [ ] **Run the missing SQL-tab test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/SqlTab.test.tsx`; expect non-zero exit and `Failed to resolve import "./SqlTab"`.

3. - [ ] **Add the exactly pinned Monaco adapter and permission-aware SQL tab.** Add `"monaco-editor": "0.55.1"` to `dependencies`, run npm install to update the committed lockfile, and create an adapter interface around only `createModel`, `createEditor`, `onDidChangeModelContent`, `layout`, and disposal. The production factory imports Monaco and creates SQL-language models; it does not export Monaco objects to React. `SqlTab` receives the adapter, permission set, snapshot, mode, and draft actions. It owns a `ref` synchronisation flag so changing `sqlSnapshot` calls `model.setValue` without treating that generated update as an operator edit. With permission, changes call `editRawSql(value)`; without permission, the model/editor use read-only options and an accessible explanation says raw SQL requires `Selections.RawSql`.

   Keep a visible “Generated SQL snapshot” label in both cases. The model may display server-generated parameter placeholders but must not display raw parameter values. Use `ResizeObserver` through the adapter seam and disconnect it at unmount. The tab calls neither raw compilation nor preview directly; its only mutation is the guarded state transition already defined in Task 2. Test model creation, editor creation, event delivery, synchronisation suppression, permission gate, resize, listener disposal, editor disposal, model disposal, dialog cancel, and confirm branches with the fake—never import a real Monaco editor into happy-dom.

4. - [ ] **Run the SQL-tab tests and confirm they pass.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/selections/SqlTab.test.tsx`; expect exit 0 with Monaco lifecycle and raw-mode confirmation assertions passing.

5. - [ ] **Commit the raw SQL boundary.** Run `git add web/package.json web/package-lock.json web/src/features/selections/monacoAdapter.ts web/src/features/selections/SqlTab.tsx web/src/features/selections/SqlTab.test.tsx && git commit -m "feat: gate raw selection SQL"`.

### Task 7: Preview virtualized rows and count distinct stable keys on request

**Files:**
- Create: `web/src/features/selections/virtualGrid.ts`, `web/src/features/selections/PreviewTab.tsx`, `web/src/features/selections/PreviewTab.test.tsx`
- Modify: `web/package.json`, `web/package-lock.json`, `web/src/features/selections/SelectionWorkbench.tsx`
- Test: `web/src/features/selections/PreviewTab.test.tsx`

1. - [ ] **Write the failing preview/count test.** Inject a virtualizer adapter that returns visible virtual indices `[20, 21]`, a Query client, and a preview request deferred on its abort signal. Assert the grid renders rows 20 and 21 rather than all 100 rows, “Count distinct stable keys” causes one count request, the output reads “Distinct stable keys: 12”, changing a predicate causes no count request, and “Cancel preview” invokes cancellation that reaches the injected request signal.

2. - [ ] **Run the missing preview test and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/PreviewTab.test.tsx`; expect non-zero exit and `Failed to resolve import "./PreviewTab"`.

3. - [ ] **Add the pinned virtualizer seam and explicit count UI.** Add `"@tanstack/react-virtual": "3.13.18"` to dependencies and regenerate the lockfile. `virtualGrid.ts` wraps `useVirtualizer` behind a small `VirtualizerAdapter` returning total height and virtual items; tests inject the adapter, while production uses the library. `PreviewTab` obtains preview and count only through Task 4 Query options. Its preview key includes draft fingerprint and schema revision, so stale results cannot replace a changed draft. The visible Cancel button calls `queryClient.cancelQueries({ queryKey: previewQueryKey(draft) })`; the query function passes TanStack Query’s signal into `fetchPreview`, making cancellation reach fetch rather than merely hiding a spinner.

   Render a semantic table with a fixed header, an `aria-live` status, and virtualized body rows positioned from adapter offsets. For state mapper output, render: loading skeleton; empty “No rows match this selection”; error retry; forbidden access explanation; cancelled “Preview cancelled”; stale refresh action; and token-expired sign-in prompt. Render each preview cell through a pure display function that formats null as `NULL` and never interpolates it as HTML. Count has no effect or query subscription until the named button is pressed; on success it says exactly `Distinct stable keys: {count}` followed by “Joined rows are not counted separately.” It renders count-state errors independently of preview so cancellation cannot erase an earlier count.

4. - [ ] **Run preview and count tests and confirm they pass.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/selections/PreviewTab.test.tsx`; expect exit 0 with virtual indices, explicit distinct-key count, no-keystroke count, cancellation, and all request-state assertions passing.

5. - [ ] **Commit preview and counting.** Run `git add web/package.json web/package-lock.json web/src/features/selections/virtualGrid.ts web/src/features/selections/PreviewTab.tsx web/src/features/selections/PreviewTab.test.tsx web/src/features/selections/SelectionWorkbench.tsx && git commit -m "feat: preview and count selection keys"`.

### Task 8: Complete the cart, save flow, application mount, and coverage evidence

**Files:**
- Create: `web/src/features/selections/SelectionCart.tsx`, `web/src/features/selections/SelectionCart.test.tsx`, `web/e2e/selection-workbench.spec.ts`
- Modify: `web/src/app/App.tsx`, `web/src/app/App.test.tsx`, `web/src/features/selections/SelectionWorkbench.tsx`
- Test: `web/src/features/selections/SelectionCart.test.tsx`, `web/src/app/App.test.tsx`, `web/e2e/selection-workbench.spec.ts`

1. - [ ] **Write the failing cart and mounted-workbench tests.** Render `SelectionCart` with an unsaved draft, saved selections, typed parameter definitions, exact count, and warning summaries. Assert a named “Unsaved changes” status, “Distinct stable keys: 12”, parameter inputs whose values stay component-local, saved-selection buttons, cart removal, and a stale save response that offers reload rather than overwriting. Update `App.test.tsx` to assert the workbench’s three landmarks mount under the existing main landmark and that changing an application navigation value away from and back to “Explore and Select” retains the draft status.

2. - [ ] **Run the absent-cart tests and confirm the intended red failure.** Run `npm --prefix web test -- --run src/features/selections/SelectionCart.test.tsx src/app/App.test.tsx`; expect non-zero exit and `Failed to resolve import "../features/selections/SelectionCart"`.

3. - [ ] **Implement the right rail and mount the feature above navigation.** `SelectionCart` receives saved-selection Query data, `draftActions`, count state, warnings, and a `saveSelection` mutation. Render cart root entries with removal controls; saved selections with load controls; typed parameter fields held in `useState` and cleared after a successful save or unmount; exact count using the Task 7 wording; stale/unstable schema warnings; and a dirty status. Save serializes the visual AST or raw SQL plus local typed parameters into the Task 4 generated request and sends the saved ETag. A 412 maps to the stale panel with “Reload saved selection” and leaves the draft intact; a 403 hides save controls; a 401 offers sign-in; all other errors are visible and retryable. Raw parameter values must not be put into the draft store, query key, preferences, URL, console, or error message.

   Update `App` to mount `SelectionWorkbench` inside the existing `<main>` and to keep the workbench/draft provider above its simple navigation state. The graph navigation target is only a labelled temporary destination that leaves the workbench subtree inactive without unmounting its provider; do not add graph nodes, edges, layout, plan review, transfer monitor, or SSE code. Add the Playwright smoke test with the real browser page: it opens Explore and Select, verifies the three landmarks, opens SQL as a raw-authorized fixture, edits a Monaco line, returns to Visual Builder, confirms discard, and verifies the visual builder is restored. It is a smoke test, not part of Vitest’s coverage totals.

4. - [ ] **Run the complete frontend verification and real-browser smoke test.** Run `npm --prefix web run generate:api && npm --prefix web run typecheck && npm --prefix web run test:coverage && npm --prefix web exec playwright test e2e/selection-workbench.spec.ts`; expect exit 0, Vitest reports 100% statements, branches, functions, and lines for all non-generated `src` files, and Playwright reports one passing workbench smoke test.

5. - [ ] **Commit the integrated workbench.** Run `git add web/src/features/selections/SelectionCart.tsx web/src/features/selections/SelectionCart.test.tsx web/e2e/selection-workbench.spec.ts web/src/app/App.tsx web/src/app/App.test.tsx web/src/features/selections/SelectionWorkbench.tsx && git commit -m "feat: complete selection workbench"`.

## Self-Review

- [ ] Coverage: confirm every handwritten module listed in File Structure is imported by a same-task Vitest unit/component test and the final `test:coverage` run enforces 100% statements, branches, functions, and lines through `coverage.include`, not the obsolete `coverage.all`. Confirm happy-dom remains the test environment and no module was excluded except generated API output.
- [ ] State and safety: confirm Query owns schema, saved selections, snapshots, preview, and count; the non-persisted draft has only client interaction values; favourites/recents use injected `StateStorage` and an explicit allowlist; raw parameter values stay form-local; and no access token appears in a store, browser storage, URL, query key, test output, or log. Confirm Zustand selectors return primitives and no selector allocates an array or object without a shallow comparator.
- [ ] Behaviour: confirm nested AND/OR/NOT, every typed operator, EXISTS, known forward/reverse paths, and manual joins edit only the AST; compile, preview, and count never concatenate SQL; generated SQL is merely a snapshot; raw modification requires permission and explicit discard confirmation before visual mode; count explicitly describes distinct stable keys; and preview cancellation aborts the actual request.
- [ ] States: confirm loading, empty, error, forbidden, cancelled, stale, and token-expired have visible, accessible handling in every relevant pane, while surprising 403 responses invalidate permissions and do not retry mutations. Confirm Monaco tests mock model/editor creation, events, resize, and disposal; virtualizer tests inject the adapter; fetch, clock, and scheduler are injected where used.
- [ ] Deferrals: confirm no task implements the dependency graph, plan review/sealing screen, transfer monitor, SSE client, worker layout, or server-side selection runtime. Confirm the only graph-related behaviour is preserving the in-memory draft while navigation leaves and returns.
- [ ] Symbol-name consistency: re-read all TypeScript and TSX fragments and confirm later imports exactly match symbols defined earlier: `VisualSelection`, `operatorsFor`, `draftActions`, `SelectionRequestError`, `toRequestState`, `SelectionWorkbench`, `SqlTab`, `PreviewTab`, `SelectionCart`, `getSelectionWorkbenchSchemaUrl`, `listSelectionsUrl`, `compileSelectionUrl`, `previewSelectionUrl`, `countSelectionUrl`, and `saveSelectionUrl`. Confirm those generated names against Orval output after regeneration before committing.
