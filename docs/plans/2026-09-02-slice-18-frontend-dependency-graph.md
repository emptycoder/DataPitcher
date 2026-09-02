# DataPitcher Slice 18: Frontend Dependency Graph Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver an accessible, bounded dependency-graph view that presents a transfer plan’s child-to-parent table dependencies without duplicating server topology or relaying out for interaction-only changes.

**Architecture:** A generated-schema boundary fetches and validates immutable plan topology into TanStack Query; pure modules derive the default plan subgraph, collapse groups, enforce the visible-node policy, form semantic layout keys, and map to/from ELK. A disposable layout cache owns coordinates, while a narrow non-persisted Zustand graph-view store owns only viewport, selection, focus, expansion, and pinned overrides. React Flow components render the derived graph and accessibility affordances; a thin ELK adapter creates the vendor’s real worker.

**Tech Stack:** React 19.2.8, TypeScript 6.0.3 strict mode, Vite 8.2.2, Vitest 4.1.11 with happy-dom and V8 coverage, React Testing Library 16.3.3, TanStack Query 5.102.8, Zustand 5.0.15, `@xyflow/react` 12.11.6, `elkjs` 0.12.0, generated OpenAPI/Zod artifacts, and Playwright 1.62.1.

---

## File Structure

- `web/package.json`, `web/package-lock.json` — add the two exact graph dependency pins and their committed npm resolution.
- `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts` — extend the one transport source with the plan graph endpoint, regenerate both client artifacts, and never hand-edit output.
- `web/src/api/planDependencyGraphApi.ts`, `web/src/api/planDependencyGraphQuery.ts`, `web/src/api/planDependencyGraph.test.ts` — injected authenticated fetch, generated-Zod validation, Query options, and boundary tests.
- `web/src/graph/model.ts`, `web/src/graph/visibleSubgraph.ts`, `web/src/graph/visibleSubgraph.test.ts` — transport-independent graph model, child-to-parent traversal, grouping, and visible-size policy.
- `web/src/graph/layout.ts`, `web/src/graph/layout.test.ts` — semantic layout-key generation, non-persisted result cache, and explicitly injected scheduling seam.
- `web/src/graph/elkLayout.ts`, `web/src/graph/elkLayout.test.ts` — pure ELK conversion plus the thin worker-oriented ELK adapter.
- `web/src/stores/graphViewStore.ts`, `web/src/stores/graphViewStore.test.tsx` — private Zustand interaction state with primitive selectors and named actions.
- `web/src/graph/presentation.ts`, `web/src/graph/GraphNode.tsx`, `web/src/graph/GraphLegend.tsx`, `web/src/graph/GraphDetails.tsx`, `web/src/graph/DependencyGraphView.tsx`, `web/src/graph/DependencyGraphView.test.tsx` — state presentation, keyboard-operable graph nodes, legend, details panel, React Flow adapter, and component contracts.
- `web/src/graph/DependencyGraphScreen.tsx`, `web/src/graph/DependencyGraphScreen.test.tsx` — Query/layout/view composition with injected fetch, authentication, layout engine, cache, and scheduler.
- `web/e2e/dependency-graph-worker.spec.ts`, `web/playwright.config.ts` — production-build worker smoke test outside the unit-coverage calculation.

## Scope and Deferrals

This slice is only the schema dependency graph. It deliberately does not create the Selection Workbench, query editing or preview, plan-review controls, transfer start controls, or the SSE transfer monitor. Those features may set an active plan and mount `DependencyGraphScreen`, but this slice neither owns their state nor copies their payloads into Zustand.

The graph’s input is the authenticated `GET /api/plans/{planId}/schema-dependency-graph` contract added to the frontend OpenAPI source. The API implementation must return this exact generated contract before the screen is wired into application navigation; no component may substitute an in-memory full-schema fixture or an unvalidated transport type. The response is topology only: opaque table and foreign-key identifiers, schema/table display names, a stable topology revision, a deterministic SCC presentation identifier, plan table states, plan table IDs, and child/parent edge endpoints. It contains no layout coordinates, connection strings, row values, or selection-query text. The API-side endpoint and the host’s active-plan navigation are integration prerequisites, not a reason to build a fake Selection Workbench here.

Do not render an entire schema by default. `onlyRenderVisibleElements` must be enabled, but it removes only **off-screen** elements: a fit-view that places every table on screen defeats culling entirely. The default is the plan subgraph—plan-selected tables plus their transitive outgoing parent dependencies—and other neighbours appear only after an operator focuses a visible table and explicitly expands its dependency or dependant neighbourhood. Schemas and multi-table SCCs are collapsible. The product target is no more than roughly 200 simultaneously visible nodes; the implementation refuses an expansion above that cap and asks the operator to focus/collapse first. Roughly 400–500 simple visible nodes remains the realistic soft frame-rate ceiling for pan, zoom, and drag, not a safe default or a reason to weaken the 200-node policy.

An edge is always `child -> parent`: `orders.customer_id -> customers.id` displays as `orders —depends on→ customers`. Selecting orders may pull customers; selecting customers does not pull orders. The direction is repeated in the legend, accessible edge label, details panel, and keyboard commands; colour is never its only expression. An explicit inbound relationship may later make a table an `explicit-dependent` plan member, but this view does not enable or edit such relationships.

### Task 1: Add generated, validated plan-topology access and graph dependencies

**Files:**
- Create: `web/src/api/planDependencyGraphApi.ts`, `web/src/api/planDependencyGraphQuery.ts`, `web/src/api/planDependencyGraph.test.ts`
- Modify: `web/package.json`, `web/package-lock.json`, `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts`
- Test: `web/src/api/planDependencyGraph.test.ts`

1. - [ ] **Write the failing validated-fetch and Query-key tests.** Create `web/src/api/planDependencyGraph.test.ts` with this complete code; its first unresolved import is intentional.

   ```ts
   import { QueryClient } from '@tanstack/react-query';
   import { expect, it, vi } from 'vitest';
   import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
   import { fetchPlanDependencyGraph } from './planDependencyGraphApi';
   import { planDependencyGraphQueryOptions } from './planDependencyGraphQuery';

   const graph = { revision: 'schema-r7', plannedTableIds: ['orders'], tables: [
     { id: 'orders', schema: 'sales', name: 'orders', componentId: 'scc:sales.orders', state: 'root-selected' },
     { id: 'customers', schema: 'sales', name: 'customers', componentId: 'scc:sales.customers', state: 'required-dependency' },
   ], relationships: [{ id: 'orders-customer', name: 'FK_orders_customer', childTableId: 'orders', parentTableId: 'customers' }] };
   const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'token');

   it('validates topology before it can enter the Query cache', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify(graph), { status: 200 }));
     await expect(fetchPlanDependencyGraph('plan-1', request, authentication, new AbortController().signal)).resolves.toEqual(graph);
     await expect(new QueryClient().fetchQuery(planDependencyGraphQueryOptions('plan-1', request, authentication))).resolves.toEqual(graph);
     expect(request).toHaveBeenCalledWith('/api/plans/plan-1/schema-dependency-graph', expect.objectContaining({ headers: { Authorization: 'Bearer token' } }));
   });

   it('rejects malformed topology instead of caching it', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify({ revision: 'r1' }), { status: 200 }));
     await expect(fetchPlanDependencyGraph('plan-1', request, authentication, new AbortController().signal)).rejects.toThrow();
   });
   ```

2. - [ ] **Run the new boundary test and confirm its intended red failure.** Run `npm --prefix web test -- --run src/api/planDependencyGraph.test.ts`; expect non-zero exit and `Failed to resolve import "./planDependencyGraphApi"`.

3. - [ ] **Pin packages, extend the contract, regenerate, and implement the small fetch boundary.** Add exactly `"@xyflow/react": "12.11.6"` and `"elkjs": "0.12.0"` to `dependencies`, run `npm --prefix web install`, and commit its resulting lockfile. Add operation ID `planSchemaDependencyGraph` for `GET /api/plans/{planId}/schema-dependency-graph`; define required `revision`, `plannedTableIds`, `tables`, and `relationships` fields. Table state is the closed enum `unselected | root-selected | required-dependency | explicit-dependent | target-satisfied | blocked | conflict | cycle-member`; relationship endpoints are required `childTableId` and `parentTableId`. `componentId` is a deterministic canonical SCC presentation key, never Tarjan’s traversal-assigned integer. Run `npm --prefix web run generate:api` before adding the following complete handwritten modules, using the generator’s emitted `getPlanSchemaDependencyGraphUrl` and `PlanSchemaDependencyGraphResponse` names.

   ```ts
   // web/src/api/planDependencyGraphApi.ts
   import type { AuthenticationAdapter } from '../auth/authAdapter';
   import { getPlanSchemaDependencyGraphUrl } from './generated/client';
   import { PlanSchemaDependencyGraphResponse } from './generated/permissions.zod';
   import { parseJson } from './parseJson';
   import type { RequestFunction } from './effectivePermissionsApi';

   export async function fetchPlanDependencyGraph(planId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
     const token = await authentication.getAccessToken();
     if (!token) throw new Error('Not authenticated.');
     return parseJson(await request(getPlanSchemaDependencyGraphUrl(planId), { headers: { Authorization: `Bearer ${token}` }, signal }), PlanSchemaDependencyGraphResponse);
   }

   // web/src/api/planDependencyGraphQuery.ts
   import type { AuthenticationAdapter } from '../auth/authAdapter';
   import type { RequestFunction } from './effectivePermissionsApi';
   import { fetchPlanDependencyGraph } from './planDependencyGraphApi';

   export function planDependencyGraphQueryOptions(planId: string, request: RequestFunction, authentication: AuthenticationAdapter) {
     return { queryKey: ['planDependencyGraph', planId] as const, staleTime: 30_000, retry: false,
       queryFn: ({ signal }: { signal: AbortSignal }) => fetchPlanDependencyGraph(planId, request, authentication, signal) };
   }
   ```

   The validated response is Query-owned refetchable server state. It is never written to a Zustand store, persisted browser storage, an URL, or a layout cache. The frontend OpenAPI file remains the source for both generated artifacts; do not handwrite a duplicate response interface.

4. - [ ] **Run typecheck, the boundary test, and generation drift check.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/api/planDependencyGraph.test.ts && npm --prefix web run generate:api`; expect exit 0, both assertions passing, and no generated-file change after the final command.

5. - [ ] **Commit the graph transport boundary.** Run `git add web/package.json web/package-lock.json web/openapi/datapitcher.openapi.json web/src/api/generated web/src/api/planDependencyGraphApi.ts web/src/api/planDependencyGraphQuery.ts web/src/api/planDependencyGraph.test.ts && git commit -m "feat: add validated plan graph query"`.

### Task 2: Derive the bounded, collapsible child-to-parent visible subgraph

**Files:**
- Create: `web/src/graph/model.ts`, `web/src/graph/visibleSubgraph.ts`, `web/src/graph/visibleSubgraph.test.ts`
- Modify: none
- Test: `web/src/graph/visibleSubgraph.test.ts`

1. - [ ] **Write the failing pure-derivation tests.** Create `web/src/graph/visibleSubgraph.test.ts` with this complete test; its imports are deliberately absent. The fixture proves the outbound parent relationship separately from the inbound dependant.

   ```ts
   import { expect, it } from 'vitest';
   import type { GraphTopology } from './model';
   import { deriveVisibleSubgraph, evaluateExpansion } from './visibleSubgraph';

   const topology: GraphTopology = { revision: 'r1', plannedTableIds: ['orders'], tables: [
     { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'root-selected' },
     { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'required-dependency' },
     { id: 'order-lines', schema: 'sales', name: 'order-lines', componentId: 'lines', state: 'unselected' },
   ], relationships: [
     { id: 'orders-customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' },
     { id: 'lines-orders', name: 'FK_lines_orders', childTableId: 'order-lines', parentTableId: 'orders' },
   ] };

   it('excludes inbound dependants until an operator expands orders', () => {
     expect(deriveVisibleSubgraph(topology, [], [], []).tableIds).toEqual(['customers', 'orders']);
     expect(deriveVisibleSubgraph(topology, ['orders'], [], []).tableIds).toEqual(['customers', 'order-lines', 'orders']);
   });
   it('refuses the 201st simultaneously visible table', () => {
     expect(evaluateExpansion(Array.from({ length: 200 }, (_, i) => `t${i}`), ['one-more']).allowed).toBe(false);
   });
   ```

2. - [ ] **Run the pure tests and confirm the intended red failure.** Run `npm --prefix web test -- --run src/graph/visibleSubgraph.test.ts`; expect non-zero exit and `Failed to resolve import "./visibleSubgraph"`.

3. - [ ] **Implement the model, default traversal, group collapse, and cap policy.** Create `model.ts` with these complete public types, then keep all traversal and presentation-independent grouping in `visibleSubgraph.ts`.

   ```ts
   export type GraphTableState = 'unselected' | 'root-selected' | 'required-dependency' | 'explicit-dependent' | 'target-satisfied' | 'blocked' | 'conflict' | 'cycle-member';
   export type GraphTable = Readonly<{ id: string; schema: string; name: string; componentId: string; state: GraphTableState }>;
   export type GraphRelationship = Readonly<{ id: string; name: string; childTableId: string; parentTableId: string }>;
   export type GraphTopology = Readonly<{ revision: string; plannedTableIds: readonly string[]; tables: readonly GraphTable[]; relationships: readonly GraphRelationship[] }>;
   export type VisibleItem = Readonly<{ id: string; kind: 'table' | 'schema' | 'scc'; memberIds: readonly string[] }>;
   export type VisibleRelationship = Readonly<{ id: string; name: string; childItemId: string; parentItemId: string }>;
   export type VisibleSubgraph = Readonly<{ items: readonly VisibleItem[]; relationships: readonly VisibleRelationship[]; tableIds: readonly string[] }>;
   export const maximumVisibleNodes = 200;
   ```

   Implement `visibleSubgraph.ts` with this complete direction and policy core; the grouping branch maps table endpoints to visible-item endpoints before ELK sees them.

   ```ts
   import { maximumVisibleNodes, type GraphTopology, type VisibleSubgraph } from './model';
   export function evaluateExpansion(currentIds: readonly string[], additions: readonly string[]) {
     return new Set([...currentIds, ...additions]).size <= maximumVisibleNodes ? { allowed: true as const }
       : { allowed: false as const, reason: 'Showing more than 200 tables is disabled; focus or collapse a group first. About 400–500 visible simple nodes is the frame-rate soft ceiling.' };
   }
   export function deriveVisibleSubgraph(topology: GraphTopology, expanded: readonly string[], collapsedSchemas: readonly string[], collapsedComponents: readonly string[]): VisibleSubgraph {
     const visible = new Set(topology.plannedTableIds); const queue = [...visible];
     while (queue.length) { const childId = queue.shift()!; for (const edge of topology.relationships) if (edge.childTableId === childId && !visible.has(edge.parentTableId)) { visible.add(edge.parentTableId); queue.push(edge.parentTableId); } }
     for (const id of expanded) for (const edge of topology.relationships) if (edge.childTableId === id || edge.parentTableId === id) { visible.add(edge.childTableId); visible.add(edge.parentTableId); }
     const tables = topology.tables.filter((table) => visible.has(table.id)); const componentSizes = new Map<string, number>(); const items = new Map<string, string[]>(); const itemOf = new Map<string, string>();
     for (const table of tables) componentSizes.set(table.componentId, (componentSizes.get(table.componentId) ?? 0) + 1);
     for (const table of tables) { const id = collapsedComponents.includes(table.componentId) && componentSizes.get(table.componentId)! > 1 ? `scc:${table.componentId}` : collapsedSchemas.includes(table.schema) ? `schema:${table.schema}` : table.id; items.set(id, [...(items.get(id) ?? []), table.id]); itemOf.set(table.id, id); }
     const seen = new Set<string>(); const relationships = topology.relationships.flatMap((edge) => { const childItemId = itemOf.get(edge.childTableId); const parentItemId = itemOf.get(edge.parentTableId); const key = `${childItemId}|${parentItemId}`; return !childItemId || !parentItemId || childItemId === parentItemId || seen.has(key) ? [] : (seen.add(key), [{ id: edge.id, name: edge.name, childItemId, parentItemId }]); });
     return { tableIds: [...visible].sort(), items: [...items].map(([id, memberIds]) => ({ id, memberIds, kind: id.startsWith('schema:') ? 'schema' : id.startsWith('scc:') ? 'scc' : 'table' })).sort((a, b) => a.id.localeCompare(b.id)), relationships };
   }
   ```

   `deriveVisibleSubgraph` first follows only `childTableId -> parentTableId` transitively from `plannedTableIds`; it then adds direct outgoing and incoming neighbours only for IDs in `expanded`. It never follows incoming edges merely because a parent is selected. Collapse happens after membership: an explicitly collapsed schema or multi-member SCC becomes one `VisibleItem`, self-edges disappear, and equal endpoint pairs become one edge. Sort IDs ordinally before returning so referentially new but equivalent Query results derive identical values.

   The policy is deliberately conservative. Visible-element culling remains enabled later, but it culls only elements outside the viewport; it cannot make a full-schema fit-view safe. Never offer a “show whole schema” action and never automatically call fit-view over anything beyond the already bounded visible subgraph.

4. - [ ] **Run the derivation tests.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/graph/visibleSubgraph.test.ts`; expect exit 0 with the direction, explicit-neighbour, collapse, and 201-node refusal assertions passing.

5. - [ ] **Commit the pure visibility policy.** Run `git add web/src/graph/model.ts web/src/graph/visibleSubgraph.ts web/src/graph/visibleSubgraph.test.ts && git commit -m "feat: derive bounded graph subgraphs"`.

### Task 3: Separate semantic layout results from graph-view interaction state

**Files:**
- Create: `web/src/graph/layout.ts`, `web/src/graph/layout.test.ts`, `web/src/stores/graphViewStore.ts`, `web/src/stores/graphViewStore.test.tsx`
- Modify: none
- Test: `web/src/graph/layout.test.ts`, `web/src/stores/graphViewStore.test.tsx`

1. - [ ] **Write the failing cache-key and narrow-store tests.** Create the two tests to prove a semantic layout key is unchanged by a new array identity, viewport, focus, highlighting, theme, transfer progress, and pinned-coordinate drag; prove it changes for topology revision, sorted visible membership, measured size profile, and layout-options version. Test cache hit/miss and injected immediate scheduler execution. In the store test render probes using `useGraphFocusedTableId`, `useIsGraphTableSelected('orders')`, and `usePinnedGraphPosition('orders')`; assert named actions change viewport, focus, selection, explicit expansion, and one pinned override without selector-loop errors.

2. - [ ] **Run both tests and confirm their intended red failures.** Run `npm --prefix web test -- --run src/graph/layout.test.ts src/stores/graphViewStore.test.tsx`; expect non-zero exit and `Failed to resolve import "./layout"` plus `Failed to resolve import "./graphViewStore"`.

3. - [ ] **Define the layout contracts, then implement the disposable cache and private graph interaction store.** `layout.ts` first exports `LayoutPosition` (`x`, `y`), `LayoutEdgeSection` (start point, bend points, end point), and `LayoutResult` (`key`, positions keyed by visible-item identity, and edge sections keyed by relationship identity). It also exports `LayoutEngine` with `layout(key, graph, sizes): Promise<LayoutResult>`, the generic injected `LayoutScheduler` function type, and `LayoutCoordinator` with `request(key, graph, sizes): Promise<LayoutResult>`, where `graph` is `VisibleSubgraph` and `sizes` is the measured item-size record. `semanticLayoutKey({ revision, visibleItemIds, measuredSizes, optionsVersion })` sorts every identifier and serializes only those four semantic inputs. `createLayoutResultCache()` owns a private `Map<string, LayoutResult>` with `get`, `set`, and `clear`; `createLayoutCoordinator(engine, cache, scheduler)` decides whether a request runs: it returns a cache hit without calling the scheduler or engine, schedules an engine call only on a miss, and rejects a result whose own key differs from the requested semantic key rather than rendering it as current. The scheduler controls when that approved work executes. The coordinator does not observe React render, object identity, viewport, hover, focus, highlight, theme, progress, or pinned positions. No clock or timer is needed; if a future debounce/expiry is added, inject its clock and scheduler rather than calling ambient browser timing APIs.

   `graphViewStore.ts` is a separate, non-persisted Zustand store. Store `viewport` as `{ x, y, zoom }`, one focused ID, selected-ID membership, expanded-table membership, collapsed-schema membership, collapsed-component membership, and pinned `{ x, y }` values. Export only named `graphViewActions` and selectors returning a primitive or an existing state value: `useGraphViewport`, `useGraphFocusedTableId`, `useIsGraphTableSelected`, `useIsGraphTableExpanded`, `useIsSchemaCollapsed`, `useIsComponentCollapsed`, and `usePinnedGraphPosition`. Do not export the underlying hook, a complete state object, a fresh array/object selector, a generic patch action, or persistence middleware. `setPinnedGraphPosition` changes coordinates for rendering but cannot change the layout key; `clearPinnedGraphPosition` restores ELK’s cached base position.

4. - [ ] **Run the cache and store tests.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/graph/layout.test.ts src/stores/graphViewStore.test.tsx`; expect exit 0 with cache semantics and all primitive-selector interaction assertions passing.

5. - [ ] **Commit the three-way ownership boundary.** Run `git add web/src/graph/layout.ts web/src/graph/layout.test.ts web/src/stores/graphViewStore.ts web/src/stores/graphViewStore.test.tsx && git commit -m "feat: separate graph layout and interaction state"`.

### Task 4: Run the layered ELK layout through the real minified worker build

**Files:**
- Create: `web/src/graph/elkLayout.ts`, `web/src/graph/elkLayout.test.ts`
- Modify: none
- Test: `web/src/graph/elkLayout.test.ts`

1. - [ ] **Write the failing ELK conversion and adapter tests.** Create `web/src/graph/elkLayout.test.ts` to assert that `orders -> customers` becomes ELK edge `sources: ['orders']`, `targets: ['customers']`; the root graph has `elk.algorithm: layered`, `elk.direction: RIGHT`, `elk.edgeRouting: ORTHOGONAL`, greedy layered cycle breaking, 40 node spacing, and 80 between-layer spacing. Mock `elkjs/lib/elk-api.js` and `elkjs/lib/elk-worker.min.js?worker`, invoke the adapter, assert its worker factory constructed the worker, assert result coordinates and edge sections are mapped, and assert `dispose` calls `terminateWorker`.

2. - [ ] **Run the adapter test and confirm its intended red failure.** Run `npm --prefix web test -- --run src/graph/elkLayout.test.ts`; expect non-zero exit and `Failed to resolve import "./elkLayout"`.

3. - [ ] **Implement the pure conversion and thin worker adapter.** Use this complete core; `toElkGraph` and `fromElkLayout` are pure and own all conversion coverage, while the constructor is the only vendor shell. `fromElkLayout(key, result)` converts returned ELK child `id`, `x`, and `y` values into the `LayoutResult` positions record and each returned edge's sections into the `LayoutResult` edge-sections record, retaining the supplied semantic key. The adapter's `layout(key, graph, sizes)` returns `fromElkLayout(key, await elk.layout(toElkGraph(graph, sizes)))`.

   ```ts
   import ELK from 'elkjs/lib/elk-api.js';
   import ElkWorker from 'elkjs/lib/elk-worker.min.js?worker';
    import type { ElkNode } from 'elkjs/lib/elk-api.js';
    import type { LayoutResult } from './layout';
   import type { VisibleSubgraph } from './model';

   export const layoutOptions = { version: 'dependency-graph-v1', 'elk.algorithm': 'layered', 'elk.direction': 'RIGHT',
     'elk.edgeRouting': 'ORTHOGONAL', 'elk.spacing.nodeNode': '40', 'elk.layered.spacing.nodeNodeBetweenLayers': '80',
     'elk.layered.cycleBreaking.strategy': 'GREEDY' } as const;

    export function toElkGraph(graph: VisibleSubgraph, sizes: Readonly<Record<string, { width: number; height: number }>>): ElkNode {
      return { id: 'root', layoutOptions, children: graph.items.map((item) => ({ id: item.id, width: sizes[item.id]!.width, height: sizes[item.id]!.height })),
       edges: graph.relationships.map((edge) => ({ id: edge.id, sources: [edge.childItemId], targets: [edge.parentItemId] })) };
    }
    export function fromElkLayout(key: string, result: ElkNode): LayoutResult { /* map ELK child coordinates and edge sections into the layout contract */ }
    export function createElkLayoutAdapter() {
      const elk = new ELK({ workerFactory: () => new ElkWorker() });
      return { layout: async (key: string, graph: VisibleSubgraph, sizes: Readonly<Record<string, { width: number; height: number }>>) => fromElkLayout(key, await elk.layout(toElkGraph(graph, sizes))),
        dispose: () => elk.terminateWorker() };
    }
   ```

   Do not import `elkjs/lib/elk.bundled.js`, create a hand-rolled layout worker, build a filesystem path from `import.meta.url`, or move ELK work into a React render. `elk-api.js` is ELK’s worker-oriented entry and Vite’s `?worker` import turns `elk-worker.min.js` into a real browser Worker factory. Retain ELK’s default node placement strategy; no incremental layout is introduced. The screen requests full layout only on a semantic key cache miss caused by topology revision, visible membership, measured dimensions, options version, or an explicit Relayout command.

4. - [ ] **Run the adapter test and a production build.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/graph/elkLayout.test.ts && npm --prefix web run build`; expect exit 0, mocked worker-factory assertions passing, and Vite resolving the minified worker asset without a main-thread ELK bundle import.

5. - [ ] **Commit the worker layout seam.** Run `git add web/src/graph/elkLayout.ts web/src/graph/elkLayout.test.ts && git commit -m "feat: add worker-backed graph layout"`.

### Task 5: Render an accessible, cull-enabled graph with details and keyboard focus

**Files:**
- Create: `web/src/graph/presentation.ts`, `web/src/graph/GraphNode.tsx`, `web/src/graph/GraphLegend.tsx`, `web/src/graph/GraphDetails.tsx`, `web/src/graph/DependencyGraphView.tsx`, `web/src/graph/DependencyGraphView.test.tsx`
- Modify: none
- Test: `web/src/graph/DependencyGraphView.test.tsx`

1. - [ ] **Write the failing React Flow adapter and accessibility tests.** Mock `@xyflow/react` before importing the view, capture its props, and invoke `onNodeClick`, `onNodeDragStop`, and `onMoveEnd`. Render the view with orders, customers, order-lines, every table state, and a selected orders node. Assert the legend’s accessible text says `Child — depends on → Parent`; every state has its text badge and icon; focused orders has a details heading; the edge has an accessible `orders depends on customers` label; the captured Flow prop is `onlyRenderVisibleElements: true`; and arrow-right from orders focuses customers while arrow-left returns to a dependant. Assert the Relayout button is the sole explicit request path and no pan, zoom, hover, focus, selection, or pinned drag invocation calls it.

2. - [ ] **Run the component test and confirm its intended red failure.** Run `npm --prefix web test -- --run src/graph/DependencyGraphView.test.tsx`; expect non-zero exit and `Failed to resolve import "./DependencyGraphView"`.

3. - [ ] **Implement rendering-only components and the graph-library adapter.** `presentation.ts` maps all eight closed states to an icon, exact label, and border class: `○ Unselected`, `● Root selected`, `↗ Required dependency`, `↘ Explicit dependent`, `✓ Target satisfied`, `! Blocked`, `⚠ Conflict`, and `⟲ Cycle member`. `GraphNode` renders a labelled `<button>` containing its icon, text badge, qualified table name, and visible border; state text and icons remain present even when CSS colours are unavailable. It handles Enter/Space selection, ArrowRight parent dependency focus, ArrowLeft dependant focus, Home first visible item, and End last visible item. Native Tab remains available between controls.

   `GraphLegend` renders the same edge phrase and explains that the arrow points from child to required parent. `GraphDetails` is an `aside` with a heading, state badge, schema-qualified identity, and two buttons: `Expand dependencies` and `Expand dependants`. It must explain that expanding dependants only reveals schema context and does not select or transfer those rows. `DependencyGraphView` receives already-derived items, relationships, positions, and named event callbacks; it has no Query, worker, timer, or Zustand import. Hoist `nodeTypes` and `edgeTypes` to module scope, memoize custom node/edge components, use stable callbacks, and render React Flow with `onlyRenderVisibleElements`, `fitView={false}`, labelled controls, and an arrow-marked custom edge. The custom edge uses the ELK section points for an orthogonal SVG path and exposes the text edge label; it never conveys direction by colour alone.

   Do not subscribe a component to the whole node/edge array. The view reads primitive Zustand selectors through a parent bridge, merges base ELK coordinates with the one pinned override in `useMemo`, and writes viewport only from `onMoveEnd` and a pin only from `onNodeDragStop`. Panning, zooming, hover, focus, highlighting, selection, dragging a pinned node, theme changes, panel sizing, React rendering, referentially new equivalent Query values, and progress updates must never request ELK layout. A visible `Relayout` control is the only operator-triggered exception. There is no full-schema fit action; a fit command, if later requested, must target only the currently capped subgraph.

4. - [ ] **Run the accessible adapter test.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/graph/DependencyGraphView.test.tsx`; expect exit 0 with the callback mock, culling, state-description, legend, side-panel, keyboard, and no-spurious-relayout assertions passing.

5. - [ ] **Commit the accessible graph renderer.** Run `git add web/src/graph/presentation.ts web/src/graph/GraphNode.tsx web/src/graph/GraphLegend.tsx web/src/graph/GraphDetails.tsx web/src/graph/DependencyGraphView.tsx web/src/graph/DependencyGraphView.test.tsx && git commit -m "feat: render accessible dependency graph"`.

### Task 6: Compose Query, semantic layout, interaction state, and production-worker smoke coverage

**Files:**
- Create: `web/src/graph/DependencyGraphScreen.tsx`, `web/src/graph/DependencyGraphScreen.test.tsx`, `web/playwright.config.ts`, `web/e2e/dependency-graph-worker.spec.ts`
- Modify: none
- Test: `web/src/graph/DependencyGraphScreen.test.tsx`, `web/e2e/dependency-graph-worker.spec.ts`

1. - [ ] **Write the failing composition test.** Create `DependencyGraphScreen.test.tsx` with a QueryClient wrapper, injected `RequestFunction`, development authentication adapter, immediate layout scheduler, in-memory `createLayoutResultCache`, and mocked ELK adapter. Assert a valid topology appears as the default orders/customers graph; assert an equivalent refetch hits the same semantic cache key; change each permitted trigger (revision, expanded membership, measured size, options version, explicit relayout) one at a time and assert one new layout; then invoke every prohibited trigger and assert no new layout. Assert missing plan context renders an accessible `Choose a transfer plan to view its dependencies.` empty state without making a request.

2. - [ ] **Run the composition test and confirm its intended red failure.** Run `npm --prefix web test -- --run src/graph/DependencyGraphScreen.test.tsx`; expect non-zero exit and `Failed to resolve import "./DependencyGraphScreen"`.

3. - [ ] **Implement the thin composition screen and browser smoke.** `DependencyGraphScreen` accepts `planId: string | null`, injected request/authentication, injected layout adapter/cache/scheduler, and no server payload prop. For a plan ID it uses `useQuery(planDependencyGraphQueryOptions(...))`, passes the validated result into `deriveVisibleSubgraph`, obtains measured node sizes through the React Flow measurement adapter, calculates the semantic key, and asks the coordinator exactly once per key miss. It uses graph-view named primitive selectors/actions for focus, selection, collapse, expansion, viewport, and pins. It clears the disposable layout cache when `planId` changes or unmounts, and disposes the ELK adapter on unmount. It does not persist or mutate Query topology.

   Add a Playwright config that starts `npm --prefix web run build && npm --prefix web exec vite preview -- --host 127.0.0.1 --port 4173`. The smoke spec intercepts the generated graph endpoint with the same minimal valid topology, opens the graph host route supplied by application composition, waits for the `orders depends on customers` edge label, and asserts that the worker-backed layout gives two distinct node positions. It must not use `import.meta.url` to construct filesystem paths; read the Vite manifest only through the test runner’s configured project path if an asset assertion is necessary. This is the required production-build integration check for ELK’s browserified worker, not a substitute for the mocked unit adapter tests.

4. - [ ] **Run all frontend checks and the production smoke.** Run `scripts/test-frontend.sh && npm --prefix web run build && npm --prefix web exec playwright test e2e/dependency-graph-worker.spec.ts`; expect exit 0, four 100% Vitest totals for handwritten `src` modules, and a browser assertion that the minified ELK worker laid out the intercepted topology.

5. - [ ] **Commit the composed screen and smoke test.** Run `git add web/src/graph/DependencyGraphScreen.tsx web/src/graph/DependencyGraphScreen.test.tsx web/playwright.config.ts web/e2e/dependency-graph-worker.spec.ts && git commit -m "feat: compose dependency graph screen"`.

## Self-Review

- [ ] Confirm the unit lane remains happy-dom, never jsdom; Vitest uses `coverage.include` rather than `coverage.all`; generated API output and tests are excluded while every handwritten `.ts` and `.tsx` module introduced above is covered in the same task. Run `scripts/test-frontend.sh` and require 100% statements, branches, functions, and lines.
- [ ] Confirm ownership is three-way: only generated-Zod-validated topology resides in TanStack Query; only disposable semantic-keyed ELK results reside in the layout cache; only viewport, selection, focus, expansion/collapse, and pinned coordinate overrides reside in non-persisted Zustand. Confirm no ambient `localStorage`, transport type, full payload, token, or generic store patch is introduced.
- [ ] Confirm the visible default is selected plan tables plus transitive outgoing parents, explicit neighbour expansion is required for all other nodes, schema/SCC collapse works, the 200-node cap is enforced, and culling is documented as off-screen-only. Confirm no default full schema or full-schema fit-view exists and the 400–500 number is stated only as a soft degradation ceiling.
- [ ] Confirm every edge is child-to-parent in generated contract, pure derivation, ELK sources/targets, arrow marker, legend, keyboard movement, details text, and tests. Confirm all eight node states use text, icon, badge, and border rather than colour alone; controls and details are reachable by role/name and keyboard.
- [ ] Confirm ELK imports `elkjs/lib/elk-api.js` and `elkjs/lib/elk-worker.min.js?worker`, never `elk.bundled.js`; options specify layered/right/orthogonal/greedy cycle breaking; and the coordinator’s only relayout triggers are topology revision, visible membership, measured size profile, options version, explicit relayout, or cache miss caused by one of them. Confirm the production worker smoke remains required.
- [ ] Confirm Selection Workbench, plan review, transfer monitor, relationship editing, incremental layout, unrestricted full-schema rendering, and API endpoint implementation/navigation composition beyond the defined contract are deferred. Re-read all TypeScript and TSX fragments for strict-mode coherence and symbol-name consistency: generated names are `getPlanSchemaDependencyGraphUrl` and `PlanSchemaDependencyGraphResponse`; later tasks use only types and functions defined in an earlier task or their own task.
