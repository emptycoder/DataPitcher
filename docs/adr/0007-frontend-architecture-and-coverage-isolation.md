# ADR 0007: Frontend Architecture and Coverage Isolation

## Title

Frontend architecture isolates server, interaction, adapter, and derived layout state so the dependency graph remains usable and handwritten code can meet the coverage requirement.

## Status

Accepted.

## Date

2026-09-01.

## Context

DataPitcher's React 19, strict-TypeScript, Vite frontend uses Zustand 5 for local workflow and UI state; TanStack Query for server state; Tailwind CSS 4; @xyflow/react; elkjs; Monaco Editor; TanStack Table and TanStack Virtual; Zod; Vitest with React Testing Library; and Playwright. Its desktop-first workflow proceeds through Connections, Explore and Select, Dependencies and Plan, and Transfer.

The centrepiece is a graph of database tables and child-to-parent foreign-key edges. It must remain useful for schemas above 1,000 nodes and several thousand edges. Handwritten frontend code has a hard requirement for 100% statement, branch, function, and line coverage.

## Decision

### 1. Render only the useful schema subgraph

Enable React Flow's `onlyRenderVisibleElements`, but do not treat it as a large-graph solution: it culls off-screen elements only, so fit-view can display all 1,000 nodes. The target is at most roughly 200 simultaneously visible nodes. The soft ceiling is approximately 400 to 500 simple nodes, or 1,000 to 2,000 visible SVG edges. Degradation appears first in pan, zoom, and drag frame rate; then selection updates; then initial mount.

The default graph is the transfer-plan subgraph: selected tables and transitive parent dependencies. Other neighbours appear only through explicit focus and expand actions. Schemas and strongly connected components are collapsible. Memoize custom node and edge components and option objects; keep callbacks stable; hoist node-type and edge-type maps to module scope; and never subscribe a component to complete nodes or edges arrays.

### 2. Run ELK layout in a real worker and recompute rarely

Use ELK's worker-oriented API entry point with its minified worker build, not the main-thread bundled build. In Vite, import the worker with the worker query suffix and pass it through ELK's worker-factory option; a URL-suffixed import with an explicit worker URL is an acceptable alternative. Layout receives the graph and layout options and returns a promise.

Set the layered algorithm, rightward direction, orthogonal routing, node-to-node and between-layer spacing, and greedy cycle breaking. Retain ELK's default node-placement strategy unless benchmarks justify changing it.

Relayout only when topology revision, visible-subgraph membership, measured node dimensions, or layout options change, or when the operator requests it. Never relayout for React render, a referentially new but semantically equivalent query result, pan or zoom, hover, focus, highlighting, dragging a pinned node, theme change, panel resize, or transfer progress. This trigger discipline is the principal graph-performance safeguard.

Timing is an estimate: budget roughly two to five seconds for 1,000 nodes and 5,000 edges; pathological graphs can exceed ten seconds. One synthetic local run took about 2.3 seconds. Full recomputation is acceptable; incremental layout is not initially needed. ELK's browserified worker build is the principal integration risk and requires a production-build smoke test.

### 3. Maintain a three-way graph data contract

Validated topology belongs in the TanStack Query cache as refetchable server state with freshness semantics. Computed base coordinates belong in a separate non-persisted layout-result cache, keyed semantically, as disposable derived data. Viewport, selected identifiers, focus, and pinned coordinate overrides belong in Zustand as client-owned interaction state.

The layout key comprises topology revision or hash, visible node set, node-size profile, and layout-options version. The worker executes only on a cache miss or key change, never from rendering or object identity. Rendering memoizes the merge of topology, base positions, and pinned overrides.

### 4. Enforce the state boundary structurally

If an authoritative value can be refetched and has freshness and invalidation semantics, it belongs in TanStack Query. If it is client-owned interaction state shared across components, it belongs in Zustand. Otherwise it remains component-local.

The session store is private, non-persisted, and composed from typed workflow, graph-view, and UI slices holding only identifiers and small value objects. It exports narrow selectors and actions, never a generic data bag or arbitrary patch action. Persisted preferences use a separate small store with persistence middleware and an explicit `partialize` allowlist; omitting `partialize` persists the entire state, the accident this structure prevents.

Large server payloads and preview datasets remain in the non-persisted Query cache. Access tokens remain in the authentication provider's memory closure. Database secrets and raw sensitive SQL parameter values remain form-local until submission, then clear. Store modules must not import transport data-transfer types, preventing large payload shapes from becoming storable.

### 5. Write Server-Sent Events into the Query cache

Job-progress events update the TanStack Query cache immutably; job state is never duplicated into Zustand. The client fetches with Authorization, event-stream Accept, and abort signal; consumes the response body reader; and decodes incrementally in streaming mode, preserving partial lines. It parses event-stream comments, multi-line data, blank-line dispatch, identifier, and retry fields, then validates every payload with Zod before cache insertion.

Sequence identifiers are monotonic and the stream identifier equals the sequence. Discard a sequence at or below the accepted watermark. A gap triggers canonical job refetch; reconnect sends the last-event identifier. On a 401, reacquire the token once and reconnect; a second consecutive 401 is terminal authentication failure. A 403 stops permanently and invalidates cached permissions. Network failures, 5xx, and unexpected end-of-stream use bounded jittered backoff. A terminal job state ends reconnection. Cleanup aborts fetch, cancels and releases the reader, clears timers, and rejects late callbacks.

### 6. Treat permission-aware UI as user experience only

Fetch the effective permission set through a Query keyed by principal, tenant, and resource scope. Do not persist it. Use short staleness, refetch on window focus, invalidate after role or subject changes and any 403, and clear it on logout.

A permission denial hides a protected control. A workflow prerequisite, validation failure, or busy state disables an otherwise permitted control and explains why. These checks are user experience only: every protected server action independently authorizes the current principal against the current resource at execution time.

For a surprising 403, roll back optimistic state, do not automatically retry the mutation, invalidate permission and resource queries, display a message that does not expose authorization detail, and re-render the control hidden.

### 7. Achieve coverage through strict adapter isolation

Exclude the vendor ELK worker build as third-party code. Test graph-to-ELK conversion as a pure module and handwritten worker shells with Vitest's web-worker helper, which executes worker modules in the same thread for coverage. Mock Monaco model and editor creation, events, resize, and disposal, then retain one real Playwright smoke test. Mock React Flow to capture and invoke every render callback, test custom nodes with React Testing Library, and smoke-test real navigation in Playwright.

Inject fetch, token provider, clock, scheduler, and randomness into SSE reconnect logic. Drive chunked readable-stream fixtures with fake timers through every branch. Cover error boundaries with a throwing child and fallback, logging, and reset assertions. Cover Suspense with a controlled deferred promise in pending, resolved, and rejected states. Exclude generated API-client output; verify reproducible generation and boundary integration instead.

Coverage cannot depend on aggregating results from natively separate worker threads because that is undocumented. One hundred percent coverage adds roughly 30 to 50 percent engineering effort, needs extensive injected seams, and creates adapter tests sensitive to library upgrades. It proves execution, not correctness, graph performance, accessibility, or authorization.

### 8. Organize modules around pure logic and thin adapters

Pure modules contain parsing, transformation, reducers, policy, and key generation. React components contain rendering and accessibility only. Thin adapter shells own calls into fetch, Worker, ELK, Monaco, timers, React Flow, and the query client.

ELK graph, option, and result mapping is pure around an ELK adapter. The SSE parser and job reducer are pure around fetch and reconnect transport. Query-AST-to-display-row transformation is pure before the grid component. Permission evaluation is pure around the permissions Query hook.

### 9. Keep generated OpenAPI client and Zod schemas

Keep both artifacts. The generated client provides endpoint calls and compile-time transport typing; generated Zod schemas validate runtime input at the trust boundary, which TypeScript cannot do after its types vanish. OpenAPI is the single transport source of truth and generates both. Parse responses and SSE payloads before Query-cache insertion, then expose inferred types or mapped domain models. Do not handwrite duplicate transport types. Handwritten refinements may wrap generated schemas without restating fields. CI regenerates and fails on drift.

### 10. Pin frontend versions

Commit the npm lockfile with the pinned compatibility set listed below. No listed package declares a React 19 peer incompatibility. Tailwind 4 requires its own Vite plugin; a Tailwind 3 setup must not be reused. Vite 8 requires Node 20.19 or later, or 22.12 or later; Node 24 LTS is preferred.

## Pinned Versions

- React and React DOM 19.2.8.
- Vite 8.2.2 and the React Vite plugin 6.1.1.
- Zustand 5.0.15 and TanStack React Query 5.102.8.
- @xyflow/react 12.11.6 and elkjs 0.12.0.
- Tailwind CSS and its Vite plugin 4.3.3.
- Vitest 4.1.11 and Playwright 1.62.1.

## Consequences

The application exposes a focused, expandable graph rather than a full schema. It avoids duplicate server and job state, skips redundant layouts, and bounds UI work by semantic changes.

This architecture creates deliberately thin seams around browser APIs and vendor libraries. It increases adapter maintenance and makes graph expansion a product interaction, but keeps behaviour testable and preserves a practical coverage path.

## Alternatives Considered

Rendering the full graph by default was rejected because fit-view defeats visible-element culling. Relayout on every render or query-object change turns routine interaction into expensive worker work. Incremental ELK layout was deferred because full recomputation meets the estimate with less risk.

A single persisted Zustand store was rejected because it duplicates refetchable state and can persist secrets or large payloads. Storing job progress in Zustand creates a second authoritative copy. Native `EventSource` cannot provide the needed Authorization header and response-body handling. Client permission checks cannot secure protected actions.

Dropping Zod for TypeScript types was rejected because types do not validate runtime payloads. Testing vendor implementations directly couples tests to third-party internals and cannot reliably aggregate native worker-thread coverage.

## Verification

Production-build smoke-test ELK worker integration and exercise default, expand, collapse, pinned-node, and fit-view flows at representative subgraph sizes. Benchmark layout and interaction separately, recording visible node and edge counts.

Architecture tests and review rules must reject transport types in stores, generic store patching, persisted session state, unvalidated Query writes, and relayouts outside semantic triggers. Test layout-key cache hits and misses, state ownership, preference allowlisting, SSE ordering, gaps, reconnection, authentication, cleanup, permission invalidation, and surprising 403s.

Use unit tests for pure modules and adapter contracts, React Testing Library for components, and Playwright smoke tests for real Monaco, React Flow navigation, and worker-enabled production builds. CI regenerates OpenAPI artifacts, fails on drift, and enforces handwritten-code coverage.

## Coverage Cost

The 100% requirement needs the pure-module and adapter-shell boundary, injected time, randomness, networking, authentication, and worker seams; exhaustive branch fixtures; and ongoing repair when vendor APIs change. Expect 30 to 50 percent additional engineering effort. It evidences executed handwritten paths, not performance, accessibility, security, or server-side authorization.

## Open Questions

- What measured visible-node and visible-edge limits should replace the initial soft ceilings for the supported desktop hardware profile?
- Which exact ELK spacing values and layout-options version should become the initial product defaults after representative-schema benchmarks?
- Which generated OpenAPI toolchain will produce both the client and Zod schemas reproducibly in CI?
