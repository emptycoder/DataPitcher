# DataPitcher Slice 20: Frontend Plan Review and Transfer Monitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver an accessible, fully covered approval view for a sealed transfer plan and a verified-outcome transfer monitor driven by authenticated Server-Sent Events.

**Architecture:** TanStack Query owns plan, path, and job snapshots; Zustand never receives plan payloads, jobs, events, or tokens. Generated OpenAPI Zod schemas validate REST and SSE payloads before cache insertion; pure policy/parser/reducer modules are covered separately from rendering and injected browser adapters.

**Tech Stack:** React 19.2.8, TypeScript 6.0.3 strict mode, TanStack Query 5.102.8, Zustand 5.0.15, Zod 4.5.4, Vite 8.2.2, Vitest 4.1.11 with happy-dom 20.13.1 and V8 coverage, React Testing Library 16.3.3, Orval 8.27.0, native Fetch/ReadableStream/TextDecoder, and the existing bearer-token authentication adapter.

---

## File Structure

- `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/{client.ts,permissions.zod.ts}` — single contract and committed, coverage-excluded Orval output.
- `web/src/features/plans/{planReviewApi.ts,planReviewModel.ts,PlanReviewView.tsx,planReviewQuery.ts,PlanReviewScreen.tsx}` — validated transport, pure approval policy/export, rendering, and Query binding.
- `web/src/features/jobs/{eventStreamParser.ts,jobReducer.ts,jobEventTransport.ts,jobApi.ts,jobMonitor.ts,TransferMonitorView.tsx}` — pure SSE semantics, injected transport, canonical refetch, cache binding, and outcome rendering.
- `web/src/test/planFixtures.ts` and same-directory tests — safe wire fixtures and complete same-task coverage.

## Scope and Deferrals

This slice implements plan review and transfer monitoring only. It excludes the dependency graph, Selection Workbench, routing, and token-provider implementation; screens receive identifiers from the future workflow boundary.

The review is an approval artifact, not a mutation surface. It shows totals, mappings, order, risks, sealing, and all eight `PlanTableState` values: `Root`, `RequiredDependency`, `ExplicitDependent`, `TargetSatisfied`, `Excluded`, `Blocked`, `Conflict`, and `CycleMember`. Its inspector posts a stable-key display value, never a URL/export value.

`TargetSatisfied` is prominent: target non-key values can differ and DataPitcher does not refresh them. Refreshed values require upsert and a new plan. ADR 0002's StrictExact is committed direct-write key equality, not value equivalence or global concurrent-change absence.

The server remains authoritative and rechecks conditions before queueing; disabled Start explains, never controls.

## Corrected API Dependencies and SSE Wire Format

Task 1 includes the currently missing `GET /api/plans/{planId}/review` and `POST /api/plans/{planId}/inclusion-paths` API endpoints and their integration tests. Both endpoints are thin reads of existing plan state through `IDataPitcherApplication`; they add no plan or inclusion-path domain logic. Each route carries `.RequireAuthorization(ApiPolicyNames.PlansRead)` and performs `PlanResource` authorization with `Permissions.PlansRead` before reading application state. Add their response/request contracts and application query members only as needed to expose the review and body-only inclusion-path shapes below.

Write the API integration test first, then add these endpoints, their contract/application seams, and the fake application's existing-state responses. Run the project-scoped API integration test with coverage before beginning the frontend adapter test. The review and inclusion-path endpoints must return 401 without credentials, 403 for a denied `PlanResource`, and must not invoke the application when that resource authorization fails.

SSE is a separate wire format: its payload property names are `State`, `RowsTransferred`, and `BytesTransferred`, and its state values are lowercase (`running`, `succeeded`, `verificationfailed`, and so on), as the existing stream tests pin. The event-payload OpenAPI schema, stream fixtures, parser/reducer, terminal-state list, and outcome logic must use that exact format. Do not change the SSE endpoint.

This correction supersedes stale fragments below: `JobEventPayload` requires the three PascalCase properties and its enum contains lowercase state values; the `Job` schema permits both its existing PascalCase REST states and lowercase event-applied states. The reducer reads `event.payload.State`, `event.payload.RowsTransferred`, and `event.payload.BytesTransferred`. The coverage command for this endpoint task is `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --collect:"XPlat Code Coverage" --results-directory artifacts/slice-20-api-coverage -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover`.

### Generator symbol-name rule

Use the names emitted by `npm --prefix web run generate:api`, not names inferred from component schemas or operation descriptions. In the current generated output they are `PlanReviewResponse`, `PlanInclusionPathResponse`, `StartPlanJobResponse`, `JobResponse`, and `JobEventsResponse`. `InclusionPathResponse`, `OperationReceiptResponse`, and `JobEventPayloadResponse` are not generated symbols and must not be imported, recreated in generated output, or induced by generator configuration changes.

`JobEventsResponse` is `zod.unknown()` because the route returns `text/event-stream`; it is not a validator for individual `data:` frames. The current generator does not emit a runtime schema for the unreferenced `JobEventPayload` component. Do not hand-edit the generated files. This blocks Task 5's generated-payload-validation requirement until a separately approved source-contract or generator-supported solution is specified.

## Tasks

### Task 1: Generate validated review and monitoring transport contracts

**Files:**
- Create: `web/src/features/plans/planReviewApi.ts`, `web/src/features/plans/planReviewApi.test.ts`, `web/src/test/planFixtures.ts`
- Create: `tests/DataPitcher.Api.IntegrationTests/PlanReviewEndpointTests.cs`
- Modify: `src/DataPitcher.Api/Contracts/{ApiContracts.cs,IDataPitcherApplication.cs}`, `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`, `web/openapi/datapitcher.openapi.json`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts`
- Test: `web/src/features/plans/planReviewApi.test.ts`

1. - [ ] **Write the failing adapter test and safe wire fixture.** Create `web/src/test/planFixtures.ts` with `export const planId = '11111111-1111-1111-1111-111111111111';` and this complete response value, which has no token, connection string, raw selection parameter, or source-row payload.

   ```ts
   export const reviewWire = {
     planId, version: 4, canonicalHash: 'A'.repeat(64),
     seal: { status: 'sealed', invalidationReasons: [] },
     totals: { included: 12, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 4096 },
     startPreconditions: [
       { code: 'permission', satisfied: true, message: 'Transfer permission is current.' },
       { code: 'sourceHealthy', satisfied: true, message: 'Source is server-verified Healthy.' },
       { code: 'targetHealthy', satisfied: true, message: 'Target is server-verified Healthy.' },
       { code: 'schemaValid', satisfied: true, message: 'Target schema validation passed.' },
       { code: 'noBlockers', satisfied: true, message: 'No blockers remain.' },
       { code: 'safeMappings', satisfied: true, message: 'All type mappings are safe.' },
       { code: 'cycleSupported', satisfied: true, message: 'Cycle strategy is supported.' },
       { code: 'authenticated', satisfied: true, message: 'Authentication is valid.' },
     ],
     tables: [{ source: { schema: 'sales', name: 'Orders' }, target: { schema: 'sales', name: 'Orders' }, state: 'Root', transferOrder: 2, included: 9, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 3072, columns: [{ source: 'Id', target: 'Id' }] }],
     conflicts: [{ table: 'sales.Orders', policy: 'FailOnConflict', message: 'Existing target keys fail the plan.' }],
     cycles: [{ tables: ['sales.Orders', 'sales.OrderLines'], strategy: 'DeferredConstraints', message: 'Constraints are deferred for this component.' }],
     warnings: [{ code: 'target-satisfied-values', message: 'Target-satisfied dependencies are not refreshed.' }],
     blockers: [],
   };
   export const inclusionPathWire = { table: 'sales.Orders', stableKey: 'Id=42', rootSelection: 'Open orders', steps: [{ relationship: 'Root selection', from: 'sales.Orders', to: 'sales.Orders', reason: 'Selected as a root row.' }] };
    export const jobWire = { jobId: '22222222-2222-2222-2222-222222222222', planId, state: 'Running', rowsTransferred: 3, bytesTransferred: 1024 };
    ```

    Before this frontend test, add the failing `PlanReviewEndpointTests.cs` integration test described in the corrected dependency section and run `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj`; it must fail because the two routes do not exist.

   Create `web/src/features/plans/planReviewApi.test.ts` with the following test body.

   ```ts
   import { expect, it, vi } from 'vitest';
   import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
   import { getPlanInclusionPathUrl, getPlanReviewUrl, getStartPlanJobUrl } from '../../api/generated/client';
   import { fetchInclusionPath, fetchPlanReview, startPlanJob } from './planReviewApi';
   import { inclusionPathWire, planId, reviewWire } from '../../test/planFixtures';

   const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
   it('sends the bearer header, validates review data, and keeps tokens out of URLs', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify(reviewWire), { status: 200 }));
     await expect(fetchPlanReview(planId, request, authentication, new AbortController().signal)).resolves.toMatchObject({ planId, version: 4 });
     expect(request).toHaveBeenCalledWith(getPlanReviewUrl(planId), expect.objectContaining({ headers: { Authorization: 'Bearer memory-token' } }));
     expect(getPlanReviewUrl(planId)).not.toContain('memory-token');
   });
   it('posts the inclusion key in the body and validates the path', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify(inclusionPathWire), { status: 200 }));
     await expect(fetchInclusionPath(planId, { table: 'sales.Orders', stableKey: 'Id=42' }, request, authentication, new AbortController().signal)).resolves.toEqual(inclusionPathWire);
     expect(request).toHaveBeenCalledWith(getPlanInclusionPathUrl(planId), expect.objectContaining({ method: 'POST', body: JSON.stringify({ table: 'sales.Orders', stableKey: 'Id=42' }) }));
   });
   it('starts with an in-memory token and rejects an absent token', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify({ operationId: '33333333-3333-3333-3333-333333333333', state: 'queued', jobId: '22222222-2222-2222-2222-222222222222' }), { status: 202 }));
     await expect(startPlanJob(planId, 'request-1', request, authentication, new AbortController().signal)).resolves.toMatchObject({ state: 'queued' });
     expect(request).toHaveBeenCalledWith(getStartPlanJobUrl(planId), expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer memory-token', 'Idempotency-Key': 'request-1' }) }));
     await authentication.signOut(); await expect(fetchPlanReview(planId, request, authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
   });
   ```

2. - [ ] **Run the test before the review adapter exists.** Run `npm --prefix web test -- --run src/features/plans/planReviewApi.test.ts`; expect non-zero exit and `Failed to resolve import "./planReviewApi"`.

3. - [ ] **Add the API endpoints, OpenAPI contract, regenerate, and implement the adapter.** Add the two tested, explicitly authorized API read endpoints before replacing `web/openapi/datapitcher.openapi.json` with this complete document, then generate instead of editing generated files. Its operation IDs produce `getPlanReviewUrl`, `getPlanInclusionPathUrl`, `getStartPlanJobUrl`, `getJobUrl`, and `getJobEventsUrl`.

   ```json
   {"openapi":"3.1.0","info":{"title":"DataPitcher API","version":"1.0.0"},"paths":{"/api/auth/effective-permissions":{"get":{"operationId":"effectivePermissions","responses":{"200":{"description":"permissions","content":{"application/json":{"schema":{"$ref":"#/components/schemas/EffectivePermissions"}}}}}}},"/api/plans/{planId}/review":{"get":{"operationId":"planReview","parameters":[{"$ref":"#/components/parameters/PlanId"}],"responses":{"200":{"description":"review","content":{"application/json":{"schema":{"$ref":"#/components/schemas/PlanReview"}}}}}}},"/api/plans/{planId}/inclusion-paths":{"post":{"operationId":"planInclusionPath","parameters":[{"$ref":"#/components/parameters/PlanId"}],"requestBody":{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/InclusionPathRequest"}}}},"responses":{"200":{"description":"path","content":{"application/json":{"schema":{"$ref":"#/components/schemas/InclusionPath"}}}}}}},"/api/plans/{planId}/jobs":{"post":{"operationId":"startPlanJob","parameters":[{"$ref":"#/components/parameters/PlanId"},{"name":"Idempotency-Key","in":"header","required":true,"schema":{"type":"string","minLength":1}}],"responses":{"202":{"description":"queued","content":{"application/json":{"schema":{"$ref":"#/components/schemas/OperationReceipt"}}}}}}},"/api/jobs/{jobId}":{"get":{"operationId":"job","parameters":[{"$ref":"#/components/parameters/JobId"}],"responses":{"200":{"description":"job","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Job"}}}}}}},"/api/jobs/{jobId}/events":{"get":{"operationId":"jobEvents","parameters":[{"$ref":"#/components/parameters/JobId"},{"name":"Last-Event-ID","in":"header","schema":{"type":"string"}}],"responses":{"200":{"description":"events","content":{"text/event-stream":{"schema":{"type":"string"}}}}}}}},"components":{"parameters":{"PlanId":{"name":"planId","in":"path","required":true,"schema":{"type":"string","format":"uuid"}},"JobId":{"name":"jobId","in":"path","required":true,"schema":{"type":"string","format":"uuid"}}},"schemas":{"EffectivePermissions":{"type":"object","required":["principalId","tenantId","permissions"],"properties":{"principalId":{"type":"string","minLength":1},"tenantId":{"type":"string","minLength":1},"permissions":{"type":"array","items":{"type":"string"}}}},"Counts":{"type":"object","required":["included","plannedWrites","inserts","updates","estimatedBytes"],"properties":{"included":{"type":"integer"},"plannedWrites":{"type":"integer"},"inserts":{"type":"integer"},"updates":{"type":"integer"},"estimatedBytes":{"type":"integer"}}},"Address":{"type":"object","required":["schema","name"],"properties":{"schema":{"type":"string"},"name":{"type":"string"}}},"ColumnMapping":{"type":"object","required":["source","target"],"properties":{"source":{"type":"string"},"target":{"type":"string"}}},"Precondition":{"type":"object","required":["code","satisfied","message"],"properties":{"code":{"type":"string","enum":["permission","sourceHealthy","targetHealthy","schemaValid","noBlockers","safeMappings","cycleSupported","authenticated"]},"satisfied":{"type":"boolean"},"message":{"type":"string"}}},"PlanTable":{"type":"object","required":["source","target","state","transferOrder","included","plannedWrites","inserts","updates","estimatedBytes","columns"],"properties":{"source":{"$ref":"#/components/schemas/Address"},"target":{"$ref":"#/components/schemas/Address"},"state":{"type":"string","enum":["Root","RequiredDependency","ExplicitDependent","TargetSatisfied","Excluded","Blocked","Conflict","CycleMember"]},"transferOrder":{"type":"integer"},"included":{"type":"integer"},"plannedWrites":{"type":"integer"},"inserts":{"type":"integer"},"updates":{"type":"integer"},"estimatedBytes":{"type":"integer"},"columns":{"type":"array","items":{"$ref":"#/components/schemas/ColumnMapping"}}}},"Message":{"type":"object","required":["code","message"],"properties":{"code":{"type":"string"},"message":{"type":"string"}}},"Conflict":{"type":"object","required":["table","policy","message"],"properties":{"table":{"type":"string"},"policy":{"type":"string"},"message":{"type":"string"}}},"Cycle":{"type":"object","required":["tables","strategy","message"],"properties":{"tables":{"type":"array","items":{"type":"string"}},"strategy":{"type":"string"},"message":{"type":"string"}}},"Seal":{"type":"object","required":["status","invalidationReasons"],"properties":{"status":{"type":"string","enum":["sealed","invalidated"]},"invalidationReasons":{"type":"array","items":{"$ref":"#/components/schemas/Message"}}}},"PlanReview":{"type":"object","required":["planId","version","canonicalHash","seal","totals","startPreconditions","tables","conflicts","cycles","warnings","blockers"],"properties":{"planId":{"type":"string","format":"uuid"},"version":{"type":"integer"},"canonicalHash":{"type":"string"},"seal":{"$ref":"#/components/schemas/Seal"},"totals":{"$ref":"#/components/schemas/Counts"},"startPreconditions":{"type":"array","items":{"$ref":"#/components/schemas/Precondition"}},"tables":{"type":"array","items":{"$ref":"#/components/schemas/PlanTable"}},"conflicts":{"type":"array","items":{"$ref":"#/components/schemas/Conflict"}},"cycles":{"type":"array","items":{"$ref":"#/components/schemas/Cycle"}},"warnings":{"type":"array","items":{"$ref":"#/components/schemas/Message"}},"blockers":{"type":"array","items":{"$ref":"#/components/schemas/Message"}}}},"InclusionPathRequest":{"type":"object","required":["table","stableKey"],"properties":{"table":{"type":"string","minLength":1},"stableKey":{"type":"string","minLength":1}}},"InclusionStep":{"type":"object","required":["relationship","from","to","reason"],"properties":{"relationship":{"type":"string"},"from":{"type":"string"},"to":{"type":"string"},"reason":{"type":"string"}}},"InclusionPath":{"type":"object","required":["table","stableKey","rootSelection","steps"],"properties":{"table":{"type":"string"},"stableKey":{"type":"string"},"rootSelection":{"type":"string"},"steps":{"type":"array","items":{"$ref":"#/components/schemas/InclusionStep"}}}},"OperationReceipt":{"type":"object","required":["operationId","state","jobId"],"properties":{"operationId":{"type":"string","format":"uuid"},"state":{"type":"string"},"jobId":{"type":"string","format":"uuid"}}},"Job":{"type":"object","required":["jobId","planId","state","rowsTransferred","bytesTransferred"],"properties":{"jobId":{"type":"string","format":"uuid"},"planId":{"type":"string","format":"uuid"},"state":{"type":"string","enum":["Draft","Queued","Preparing","Running","Pausing","Paused","Cancelling","Cancelled","Verifying","Succeeded","Failed","VerificationFailed"]},"rowsTransferred":{"type":"integer"},"bytesTransferred":{"type":"integer"}}},"JobEventPayload":{"type":"object","required":["state","rowsTransferred","bytesTransferred"],"properties":{"state":{"type":"string","enum":["Draft","Queued","Preparing","Running","Pausing","Paused","Cancelling","Cancelled","Verifying","Succeeded","Failed","VerificationFailed"]},"rowsTransferred":{"type":"integer"},"bytesTransferred":{"type":"integer"}}}}}}
   ```

   Create `web/src/features/plans/planReviewApi.ts` exactly as follows.

   ```ts
   import type { AuthenticationAdapter } from '../../auth/authAdapter';
   import { getPlanInclusionPathUrl, getPlanReviewUrl, getStartPlanJobUrl } from '../../api/generated/client';
    import { PlanInclusionPathResponse, PlanReviewResponse, StartPlanJobResponse } from '../../api/generated/permissions.zod';
   import { parseJson } from '../../api/parseJson';

   export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
   export type InclusionRequest = Readonly<{ table: string; stableKey: string }>;
   async function authorization(authentication: AuthenticationAdapter) {
     const token = await authentication.getAccessToken();
     if (!token) throw new Error('Not authenticated.');
     return { Authorization: `Bearer ${token}` };
   }
   export async function fetchPlanReview(planId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
     return parseJson(await request(getPlanReviewUrl(planId), { headers: await authorization(authentication), signal }), PlanReviewResponse);
   }
   export async function fetchInclusionPath(planId: string, body: InclusionRequest, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
      return parseJson(await request(getPlanInclusionPathUrl(planId), { method: 'POST', headers: { ...await authorization(authentication), 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal }), PlanInclusionPathResponse);
   }
   export async function startPlanJob(planId: string, idempotencyKey: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
      return parseJson(await request(getStartPlanJobUrl(planId), { method: 'POST', headers: { ...await authorization(authentication), 'Idempotency-Key': idempotencyKey }, signal }), StartPlanJobResponse);
   }
   ```

    Run `npm --prefix web run generate:api` so Orval writes both generated artifacts. Keep existing pins. The emitted Zod values are `PlanReviewResponse`, `PlanInclusionPathResponse`, `StartPlanJobResponse`, `JobResponse`, and `JobEventsResponse`; the last is `zod.unknown()` for the raw stream response, not a payload validator.

4. - [ ] **Run the transport test and typecheck.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/plans/planReviewApi.test.ts`; expect exit 0 with three passing tests.

5. - [ ] **Commit the generated contract and adapter.** Run `git add web/openapi/datapitcher.openapi.json web/src/api/generated/client.ts web/src/api/generated/permissions.zod.ts web/src/features/plans/planReviewApi.ts web/src/features/plans/planReviewApi.test.ts web/src/test/planFixtures.ts && git commit -m "feat: add plan review transport contract"`.

### Task 2: Define pure approval policy and the sanitized export

**Files:**
- Create: `web/src/features/plans/planReviewModel.ts`, `web/src/features/plans/planReviewModel.test.ts`
- Modify: none
- Test: `web/src/features/plans/planReviewModel.test.ts`

1. - [ ] **Write the failing policy and export test.** Create `web/src/features/plans/planReviewModel.test.ts` with this complete test body.

   ```ts
   import { expect, it } from 'vitest';
   import { createSanitizedPlanExport, planTableStateLabel, startAvailability } from './planReviewModel';
   import { reviewWire } from '../../test/planFixtures';

   it.each([['Root', 'Root'], ['RequiredDependency', 'Required dependency'], ['ExplicitDependent', 'Explicit dependent'], ['TargetSatisfied', 'Target satisfied'], ['Excluded', 'Excluded'], ['Blocked', 'Blocked'], ['Conflict', 'Conflict'], ['CycleMember', 'Cycle member']])('labels every plan table state', (state, label) => expect(planTableStateLabel(state as never)).toBe(label));
   it('disables stale and failed-precondition starts with server-supplied reasons', () => {
     const review = { ...reviewWire, seal: { status: 'invalidated', invalidationReasons: [{ code: 'target-schema', message: 'Target schema changed.' }] }, startPreconditions: [{ code: 'schemaValid', satisfied: false, message: 'Target schema validation failed.' }] };
     expect(startAvailability(review as never)).toEqual({ enabled: false, reasons: ['Target schema changed.', 'Target schema validation failed.'] });
   });
   it('exports only review-safe approval facts', () => {
     const exported = createSanitizedPlanExport(reviewWire as never);
     expect(exported).toContain('sales.Orders');
     expect(exported).not.toContain('Id=42');
     expect(exported).not.toContain('memory-token');
   });
   ```

2. - [ ] **Run the missing policy test.** Run `npm --prefix web test -- --run src/features/plans/planReviewModel.test.ts`; expect non-zero exit and `Failed to resolve import "./planReviewModel"`.

3. - [ ] **Implement the complete pure policy.** Create `web/src/features/plans/planReviewModel.ts` with this code. The export deliberately whitelists review facts rather than serializing a server object; an inclusion path can contain a row key and is therefore not exportable.

   ```ts
   import { z } from 'zod';
   import { PlanReviewResponse } from '../../api/generated/permissions.zod';

   export type PlanReview = z.infer<typeof PlanReviewResponse>;
   type PlanTableState = PlanReview['tables'][number]['state'];
   export function planTableStateLabel(state: PlanTableState) {
     return ({ Root: 'Root', RequiredDependency: 'Required dependency', ExplicitDependent: 'Explicit dependent', TargetSatisfied: 'Target satisfied', Excluded: 'Excluded', Blocked: 'Blocked', Conflict: 'Conflict', CycleMember: 'Cycle member' } satisfies Record<PlanTableState, string>)[state];
   }
   export function startAvailability(review: PlanReview) {
     const reasons = [
       ...(review.seal.status === 'invalidated' ? review.seal.invalidationReasons.map((reason) => reason.message) : []),
       ...review.startPreconditions.filter((check) => !check.satisfied).map((check) => check.message),
     ];
     return { enabled: review.seal.status === 'sealed' && reasons.length === 0, reasons };
   }
   export function createSanitizedPlanExport(review: PlanReview) {
     return JSON.stringify({ planId: review.planId, version: review.version, canonicalHash: review.canonicalHash, seal: review.seal, totals: review.totals, tables: review.tables.map(({ source, target, state, transferOrder, included, plannedWrites, inserts, updates, estimatedBytes, columns }) => ({ source, target, state, transferOrder, included, plannedWrites, inserts, updates, estimatedBytes, columns })), conflicts: review.conflicts, cycles: review.cycles, warnings: review.warnings, blockers: review.blockers }, null, 2);
   }
   ```

4. - [ ] **Run the pure-module test.** Run `npm --prefix web test -- --run src/features/plans/planReviewModel.test.ts`; expect exit 0 and ten passing cases.

5. - [ ] **Commit the review policy.** Run `git add web/src/features/plans/planReviewModel.ts web/src/features/plans/planReviewModel.test.ts && git commit -m "feat: add plan review policy"`.

### Task 3: Render the immutable plan-review approval artifact

**Files:**
- Create: `web/src/features/plans/PlanReviewView.tsx`, `web/src/features/plans/PlanReviewView.test.tsx`
- Modify: none
- Test: `web/src/features/plans/PlanReviewView.test.tsx`

1. - [ ] **Write the failing accessible-review test.** Create `web/src/features/plans/PlanReviewView.test.tsx` with this complete body.

   ```tsx
   import { expect, it, vi } from 'vitest';
   import { fireEvent, render, screen } from '@testing-library/react';
   import { PlanReviewView } from './PlanReviewView';
   import { inclusionPathWire, reviewWire } from '../../test/planFixtures';

   const allStates = ['Root', 'RequiredDependency', 'ExplicitDependent', 'TargetSatisfied', 'Excluded', 'Blocked', 'Conflict', 'CycleMember'];
   it('renders totals, every state, mappings, order, warnings, and the target-satisfied limitation', () => {
     const review = { ...reviewWire, tables: allStates.map((state, transferOrder) => ({ ...reviewWire.tables[0], state, transferOrder })) };
     render(<PlanReviewView review={review as never} path={null} pathLoading={false} onInspect={vi.fn()} onExport={vi.fn()} onStart={vi.fn()} />);
     expect(screen.getByText('9 planned writes')).toBeVisible();
     expect(screen.getByText('4096 estimated bytes')).toBeVisible();
     expect(screen.getByText('Target satisfied')).toBeVisible();
     expect(screen.getByText(/may hold different non-key values/i)).toBeVisible();
     expect(screen.getByText('DeferredConstraints')).toBeVisible();
     expect(screen.getByText('FailOnConflict')).toBeVisible();
   });
   it('submits a body-only path lookup, renders why the row was included, exports, and starts only when advisory checks pass', () => {
     const inspect = vi.fn(); const exported = vi.fn(); const start = vi.fn();
     render(<PlanReviewView review={reviewWire as never} path={inclusionPathWire as never} pathLoading={false} onInspect={inspect} onExport={exported} onStart={start} />);
     fireEvent.change(screen.getByLabelText('Stable key'), { target: { value: 'Id=42' } });
     fireEvent.submit(screen.getByRole('form', { name: 'Why was this row included?' }));
     fireEvent.click(screen.getByRole('button', { name: 'Export sanitized plan' }));
     fireEvent.click(screen.getByRole('button', { name: 'Start transfer' }));
     expect(inspect).toHaveBeenCalledWith({ table: 'sales.Orders', stableKey: 'Id=42' });
     expect(screen.getByText('Open orders')).toBeVisible();
     expect(exported).toHaveBeenCalledOnce();
     expect(start).toHaveBeenCalledOnce();
   });
   it('explains material invalidation while disabling Start and loading a path', () => {
     const review = { ...reviewWire, seal: { status: 'invalidated', invalidationReasons: [{ code: 'schema', message: 'Target schema changed.' }] }, startPreconditions: [{ code: 'schemaValid', satisfied: false, message: 'Schema validation failed.' }] };
     render(<PlanReviewView review={review as never} path={null} pathLoading onInspect={vi.fn()} onExport={vi.fn()} onStart={vi.fn()} />);
     expect(screen.getByText('Target schema changed.')).toBeVisible(); expect(screen.getByText('Loading inclusion path.')).toBeVisible(); expect(screen.getByRole('button', { name: 'Start transfer' })).toBeDisabled();
   });
   ```

2. - [ ] **Run the missing-view test.** Run `npm --prefix web test -- --run src/features/plans/PlanReviewView.test.tsx`; expect non-zero exit and `Failed to resolve import "./PlanReviewView"`.

3. - [ ] **Implement the rendering-only review view.** Create `web/src/features/plans/PlanReviewView.tsx`; it has no transport, Query, Zustand, storage, timer, or token access.

   ```tsx
   import { useState } from 'react';
   import type { InclusionRequest } from './planReviewApi';
   import { createSanitizedPlanExport, planTableStateLabel, startAvailability, type PlanReview } from './planReviewModel';
   import type { z } from 'zod';
    import { PlanInclusionPathResponse } from '../../api/generated/permissions.zod';

    type InclusionPath = z.infer<typeof PlanInclusionPathResponse>;
   type Props = { review: PlanReview; path: InclusionPath | null; pathLoading: boolean; onInspect: (request: InclusionRequest) => void; onExport: (value: string) => void; onStart: () => void };
   export function PlanReviewView({ review, path, pathLoading, onInspect, onExport, onStart }: Props) {
     const [stableKey, setStableKey] = useState('');
     const availability = startAvailability(review);
     return <main aria-label="Transfer plan review">
       <h1>Transfer plan version {review.version}</h1>
       <p>{review.seal.status === 'sealed' ? 'Sealed and current.' : 'Invalidated: a material change requires a new sealed version.'}</p>
       {review.seal.invalidationReasons.map((reason) => <p key={reason.code}>{reason.message}</p>)}
       <section aria-label="Exact plan totals"><h2>Exact plan totals</h2><p>{review.totals.plannedWrites} planned writes</p><p>{review.totals.estimatedBytes} estimated bytes</p><p>{review.totals.included} included rows; {review.totals.inserts} inserts; {review.totals.updates} updates.</p></section>
       <p><strong>Target satisfied:</strong> a target-satisfied dependency may hold different non-key values than the source. DataPitcher will not refresh it; choose an upsert policy and create a new plan when values must be refreshed.</p>
       <section aria-label="Plan tables"><h2>Plan tables</h2><table><thead><tr><th>State</th><th>Source → target</th><th>Writes</th><th>Bytes</th><th>Order</th></tr></thead><tbody>{review.tables.map((table) => <tr key={`${table.source.schema}.${table.source.name}`}><td>{planTableStateLabel(table.state)}</td><td>{table.source.schema}.{table.source.name} → {table.target.schema}.{table.target.name}<ul>{table.columns.map((column) => <li key={`${column.source}:${column.target}`}>{column.source} → {column.target}</li>)}</ul></td><td>{table.plannedWrites}</td><td>{table.estimatedBytes}</td><td>{table.transferOrder}</td></tr>)}</tbody></table></section>
       <section aria-label="Transfer order"><h2>Transfer order</h2><ol>{review.tables.map((table) => <li key={`order-${table.transferOrder}`}>{table.transferOrder}: {table.target.schema}.{table.target.name}</li>)}</ol></section>
       <section aria-label="Target conflicts"><h2>Conflicts</h2>{review.conflicts.map((item) => <p key={item.table}>{item.table}: {item.policy} — {item.message}</p>)}</section>
       <section aria-label="Cycles"><h2>Cycles</h2>{review.cycles.map((cycle) => <p key={cycle.tables.join('|')}>{cycle.tables.join(', ')}: {cycle.strategy} — {cycle.message}</p>)}</section>
       <section aria-label="Warnings and blockers"><h2>Warnings and blockers</h2>{review.warnings.concat(review.blockers).map((item) => <p key={item.code}>{item.message}</p>)}</section>
       <form aria-label="Why was this row included?" onSubmit={(event) => { event.preventDefault(); onInspect({ table: `${review.tables[0]!.source.schema}.${review.tables[0]!.source.name}`, stableKey }); }}><h2>Why was this row included?</h2><label>Stable key<input aria-label="Stable key" value={stableKey} onChange={(event) => setStableKey(event.target.value)} required /></label><button>Inspect inclusion path</button></form>
       {pathLoading && <p>Loading inclusion path.</p>}{path && <section aria-label="Inclusion path"><p>{path.table} {path.stableKey} began at {path.rootSelection}.</p>{path.steps.map((step) => <p key={`${step.relationship}:${step.from}`}>{step.relationship}: {step.from} → {step.to}. {step.reason}</p>)}</section>}
       <button onClick={() => onExport(createSanitizedPlanExport(review))}>Export sanitized plan</button>
       <section aria-label="Start transfer"><h2>Start transfer</h2><p>These client checks are advisory; the server rechecks every condition when Start is requested.</p>{availability.reasons.map((reason) => <p key={reason}>{reason}</p>)}<button disabled={!availability.enabled} onClick={onStart}>Start transfer</button></section>
     </main>;
   }
   ```

4. - [ ] **Run the component test and coverage lane.** Run `npm --prefix web test -- --run src/features/plans/PlanReviewView.test.tsx && npm --prefix web run test:coverage`; expect exit 0 and 100% statements, branches, functions, and lines for all handwritten frontend modules.

5. - [ ] **Commit the approval artifact.** Run `git add web/src/features/plans/PlanReviewView.tsx web/src/features/plans/PlanReviewView.test.tsx && git commit -m "feat: render transfer plan review"`.

### Task 4: Bind plan review to validated TanStack Query data

**Files:**
- Create: `web/src/features/plans/planReviewQuery.ts`, `web/src/features/plans/PlanReviewScreen.tsx`, `web/src/features/plans/PlanReviewScreen.test.tsx`
- Modify: none
- Test: `web/src/features/plans/PlanReviewScreen.test.tsx`

1. - [ ] **Write the failing Query-container test.** Create `web/src/features/plans/PlanReviewScreen.test.tsx` with this complete body.

   ```tsx
   import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
   import { expect, it, vi } from 'vitest';
   import { fireEvent, render, screen, waitFor } from '@testing-library/react';
   import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
   import { PlanReviewScreen } from './PlanReviewScreen';
   import { inclusionPathWire, planId, reviewWire } from '../../test/planFixtures';

   it('puts validated review and path data in Query and queues the server-authorized start', async () => {
     const request = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(init?.method === 'POST' && String(input).includes('inclusion-paths') ? inclusionPathWire : init?.method === 'POST' ? { operationId: '33333333-3333-3333-3333-333333333333', state: 'queued', jobId: '22222222-2222-2222-2222-222222222222' } : reviewWire), { status: init?.method === 'POST' && !String(input).includes('inclusion-paths') ? 202 : 200 }));
     const client = new QueryClient({ defaultOptions: { queries: { retry: false } } }); const queued = vi.fn(); const exported = vi.fn();
     render(<QueryClientProvider client={client}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} createId={() => 'request-1'} onJobQueued={queued} onExport={exported} /></QueryClientProvider>);
     await screen.findByRole('heading', { name: 'Transfer plan version 4' });
     fireEvent.change(screen.getByLabelText('Stable key'), { target: { value: 'Id=42' } }); fireEvent.submit(screen.getByRole('form', { name: 'Why was this row included?' }));
     await screen.findByText(/began at Open orders/i); fireEvent.click(screen.getByRole('button', { name: 'Start transfer' }));
     await waitFor(() => expect(queued).toHaveBeenCalledWith('22222222-2222-2222-2222-222222222222'));
     expect(client.getQueryData(['planReview', planId])).toMatchObject({ version: 4 });
   });
   it('renders pending then failed review requests', async () => {
     let reject!: (error: Error) => void; const request = vi.fn(() => new Promise<Response>((_, value) => { reject = value; }));
     render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><PlanReviewScreen planId={planId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} createId={() => 'request-2'} onJobQueued={vi.fn()} onExport={vi.fn()} /></QueryClientProvider>);
     expect(screen.getByText('Loading plan review.')).toBeVisible(); reject(new Error('offline')); expect(await screen.findByText('Plan review could not be loaded.')).toBeVisible();
   });
   ```

2. - [ ] **Run the missing-container test.** Run `npm --prefix web test -- --run src/features/plans/PlanReviewScreen.test.tsx`; expect non-zero exit and `Failed to resolve import "./PlanReviewScreen"`.

3. - [ ] **Implement Query factories and the container.** Create `web/src/features/plans/planReviewQuery.ts` and `web/src/features/plans/PlanReviewScreen.tsx`. The plan, inspector, and receipt are server state; no Zustand selector is used.

   ```ts
   // web/src/features/plans/planReviewQuery.ts
   import type { AuthenticationAdapter } from '../../auth/authAdapter';
   import { fetchInclusionPath, fetchPlanReview, type InclusionRequest, type RequestFunction } from './planReviewApi';
   export const planReviewKey = (planId: string) => ['planReview', planId] as const;
   export function planReviewQueryOptions(planId: string, request: RequestFunction, authentication: AuthenticationAdapter) { return { queryKey: planReviewKey(planId), retry: false, queryFn: ({ signal }: { signal: AbortSignal }) => fetchPlanReview(planId, request, authentication, signal) }; }
   export function inclusionPathQueryOptions(planId: string, value: InclusionRequest, request: RequestFunction, authentication: AuthenticationAdapter) { return { queryKey: ['inclusionPath', planId, value] as const, retry: false, queryFn: ({ signal }: { signal: AbortSignal }) => fetchInclusionPath(planId, value, request, authentication, signal) }; }
   ```

   ```tsx
   // web/src/features/plans/PlanReviewScreen.tsx
   import { useState } from 'react';
   import { useMutation, useQuery } from '@tanstack/react-query';
   import type { AuthenticationAdapter } from '../../auth/authAdapter';
   import { PlanReviewView } from './PlanReviewView';
   import { startPlanJob, type InclusionRequest, type RequestFunction } from './planReviewApi';
   import { inclusionPathQueryOptions, planReviewQueryOptions } from './planReviewQuery';

   type Props = { planId: string; request: RequestFunction; authentication: AuthenticationAdapter; createId: () => string; onJobQueued: (jobId: string) => void; onExport: (value: string) => void };
   export function PlanReviewScreen({ planId, request, authentication, createId, onJobQueued, onExport }: Props) {
     const [inspection, setInspection] = useState<InclusionRequest>({ table: '', stableKey: '' });
     const review = useQuery(planReviewQueryOptions(planId, request, authentication));
     const path = useQuery({ ...inclusionPathQueryOptions(planId, inspection, request, authentication), enabled: inspection.table.length > 0 });
     const start = useMutation({ mutationFn: (signal: AbortSignal) => startPlanJob(planId, createId(), request, authentication, signal), onSuccess: (receipt) => onJobQueued(receipt.jobId) });
     if (review.isPending) return <main aria-label="Transfer plan review"><p>Loading plan review.</p></main>;
     if (review.isError) return <main aria-label="Transfer plan review"><p>Plan review could not be loaded.</p></main>;
     return <PlanReviewView review={review.data} path={path.data ?? null} pathLoading={path.isFetching} onInspect={setInspection} onExport={onExport} onStart={() => start.mutate(new AbortController().signal)} />;
   }
   ```

4. - [ ] **Run the container test and typecheck.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/plans/PlanReviewScreen.test.tsx`; expect exit 0 with the validated Query cache and queued-job assertions passing.

5. - [ ] **Commit Query-owned plan review.** Run `git add web/src/features/plans/planReviewQuery.ts web/src/features/plans/PlanReviewScreen.tsx web/src/features/plans/PlanReviewScreen.test.tsx && git commit -m "feat: query plan review data"`.

### Task 5: Blocked — emit a runtime schema for SSE payloads

Do not create the parser, reducer, or tests yet. The `JobEventPayload` component is not emitted as a Zod schema from the `text/event-stream` response, so this task cannot validate every `data:` payload at the trust boundary without violating the generated-schema rule. Do not handwrite a duplicate schema, edit generated output, or change generator configuration to manufacture a guessed name. Resume this task only with an approved source-contract or generator-supported payload-schema solution; retain the required lowercase state values and `VerificationFailed` failure outcome when it resumes.

### Task 6: Consume authenticated SSE with deterministic reconnect and cleanup

**Files:**
- Create: `web/src/features/jobs/jobEventTransport.ts`, `web/src/features/jobs/jobEventTransport.test.ts`
- Modify: none
- Test: `web/src/features/jobs/jobEventTransport.test.ts`

1. - [ ] **Write the failing transport test with chunked streams and injected time.** Create `web/src/features/jobs/jobEventTransport.test.ts` with this complete body.

   ```ts
   import { expect, it, vi } from 'vitest';
   import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
   import { openJobEventStream } from './jobEventTransport';
   import { jobWire } from '../../test/planFixtures';
   const stream = (chunks: string[]) => new ReadableStream<Uint8Array>({ start(controller) { chunks.forEach((chunk) => controller.enqueue(new TextEncoder().encode(chunk))); controller.close(); } });
   it('reacquires once after 401, validates chunked events, and stops at VerificationFailed', async () => {
     vi.useFakeTimers();
      const request = vi.fn().mockResolvedValueOnce(new Response('', { status: 401 })).mockResolvedValueOnce(new Response(stream(['id: 1\nevent: state\ndata: {"State":"verification', 'failed","RowsTransferred":9,"BytesTransferred":40}\n\n']), { status: 200 }));
     const accepted = vi.fn(); const terminal = vi.fn(); const clock = { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() };
     const handle = openJobEventStream({ job: jobWire as never, request, authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'), clock, random: () => 0, onAccepted: accepted, onGap: vi.fn(async () => jobWire as never), onForbidden: vi.fn(), onTerminal: terminal, onError: vi.fn() });
     await handle.done;
      expect(request).toHaveBeenCalledTimes(2); expect(accepted).toHaveBeenCalledWith(expect.objectContaining({ state: 'verificationfailed' })); expect(terminal).toHaveBeenCalledOnce(); expect(clock.setTimeout).not.toHaveBeenCalled(); vi.useRealTimers();
   });
   it('refetches a gap and permanently invalidates permissions on 403', async () => {
     const gap = vi.fn(async () => jobWire as never); const forbidden = vi.fn(); const clock = { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() };
      const handle = openJobEventStream({ job: jobWire as never, request: vi.fn().mockResolvedValueOnce(new Response(stream(['id: 2\ndata: {"State":"running","RowsTransferred":4,"BytesTransferred":10}\n\n']), { status: 200 })).mockResolvedValueOnce(new Response('', { status: 403 })), authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'), clock, random: () => 0, onAccepted: vi.fn(), onGap: gap, onForbidden: forbidden, onTerminal: vi.fn(), onError: vi.fn() });
     await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); expect(gap).toHaveBeenCalledOnce(); expect(clock.setTimeout).toHaveBeenCalledOnce(); (clock.setTimeout.mock.calls[0]![0] as () => void)(); await handle.done; expect(forbidden).toHaveBeenCalledOnce(); expect(request.mock.calls[1]![1]).toEqual(expect.objectContaining({ headers: expect.objectContaining({ 'Last-Event-ID': '2' }) }));
   });
   it('stops after consecutive 401 responses', async () => {
     const clock = { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() }; const error = vi.fn(); const handle = openJobEventStream({ job: jobWire as never, request: vi.fn().mockResolvedValueOnce(new Response('', { status: 401 })).mockResolvedValueOnce(new Response('', { status: 401 })), authentication: createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'), clock, random: () => 0, onAccepted: vi.fn(), onGap: vi.fn(), onForbidden: vi.fn(), onTerminal: vi.fn(), onError: error });
     await handle.done; expect(error).toHaveBeenCalledWith('Authentication expired.'); expect(clock.clearTimeout).not.toHaveBeenCalled();
   });
   ```

2. - [ ] **Run the missing transport test.** Run `npm --prefix web test -- --run src/features/jobs/jobEventTransport.test.ts`; expect non-zero exit and `Failed to resolve import "./jobEventTransport"`.

3. - [ ] **Implement the injected fetch/reconnect shell.** Create `web/src/features/jobs/jobEventTransport.ts` with this complete code. It uses Fetch because native `EventSource` cannot attach `Authorization`; it never places the token in a URL.

   ```ts
   import type { AuthenticationAdapter } from '../../auth/authAdapter';
   import { getJobEventsUrl } from '../../api/generated/client';
   import type { RequestFunction } from '../plans/planReviewApi';
   import { EventStreamParser } from './eventStreamParser';
   import { parseJobStreamEvent, reduceJobEvent, type Job } from './jobReducer';
   type Clock = { setTimeout: (callback: () => void, milliseconds: number) => number; clearTimeout: (id: number) => void };
    type Options = { job: Job; request: RequestFunction; authentication: AuthenticationAdapter; clock: Clock; random: () => number; onAccepted: (job: Job) => void; onGap: (signal: AbortSignal) => Promise<Job>; onForbidden: () => void; onTerminal: (job: Job) => void; onError: (message: string) => void };
   export function openJobEventStream(options: Options) {
      let stopped = false; let generation = 0; let watermark = 0; let current = options.job; let refreshes = 0; let attempts = 0; let retry = 250; let timer: number | undefined; let controller: AbortController | undefined; let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
     let resolve!: () => void; const done = new Promise<void>((value) => { resolve = value; });
     const close = () => { stopped = true; generation++; controller?.abort(); void reader?.cancel(); reader?.releaseLock(); if (timer !== undefined) options.clock.clearTimeout(timer); resolve(); };
      const schedule = () => { if (!stopped) timer = options.clock.setTimeout(() => { void connect(); }, Math.min(30_000, retry * 2 ** attempts++) + Math.floor(options.random() * 100)); };
     const connect = async () => {
       const turn = ++generation; controller = new AbortController(); const token = await options.authentication.getAccessToken();
       if (stopped || turn !== generation || !token) return close();
       let response: Response; try { response = await options.request(getJobEventsUrl(current.jobId), { headers: { Authorization: `Bearer ${token}`, Accept: 'text/event-stream', ...(watermark ? { 'Last-Event-ID': String(watermark) } : {}) }, signal: controller.signal }); } catch { return schedule(); }
       if (stopped || turn !== generation) return;
        if (response.status === 401) { if (refreshes++ === 0) return void connect(); options.onError('Authentication expired.'); return close(); }
        if (response.status === 403) { options.onForbidden(); return close(); }
        if (!response.ok || !response.body) return schedule();
        refreshes = 0; attempts = 0;
       const parser = new EventStreamParser(); const decoder = new TextDecoder(); reader = response.body.getReader();
        try { for (;;) { const next = await reader.read(); if (next.done) break; for (const record of parser.push(decoder.decode(next.value, { stream: true }))) { if (record.kind === 'retry') { retry = Math.max(250, Math.min(30_000, record.milliseconds)); continue; } const event = parseJobStreamEvent(record); const result = reduceJobEvent(current, watermark, event); if (result.kind === 'duplicate') continue; if (result.kind === 'gap') { const canonical = await options.onGap(controller.signal); if (stopped || turn !== generation) return; current = canonical; watermark = event.sequence; continue; } current = result.job; watermark = result.watermark; options.onAccepted(current); if (result.kind === 'terminal') { options.onTerminal(current); return close(); } } } } catch (error) { if (!stopped) options.onError(error instanceof Error ? error.message : 'Invalid event stream.'); } finally { reader.releaseLock(); reader = undefined; }
       schedule();
     };
     void connect(); return { close, done };
   }
   ```

4. - [ ] **Run the deterministic reconnect test and the full frontend gate.** Run `npm --prefix web test -- --run src/features/jobs/jobEventTransport.test.ts && npm --prefix web run test:coverage`; expect exit 0, no real-time waiting, and all handwritten coverage totals at 100%.

5. - [ ] **Commit the authenticated transport.** Run `git add web/src/features/jobs/jobEventTransport.ts web/src/features/jobs/jobEventTransport.test.ts && git commit -m "feat: reconnect authenticated job events"`.

### Task 7: Update Query cache from monitor events and display verified outcomes

**Files:**
- Create: `web/src/features/jobs/jobApi.ts`, `web/src/features/jobs/jobMonitor.ts`, `web/src/features/jobs/TransferMonitorView.tsx`, `web/src/features/jobs/jobMonitor.test.ts`, `web/src/features/jobs/TransferMonitorView.test.tsx`
- Modify: none
- Test: `web/src/features/jobs/jobMonitor.test.ts`, `web/src/features/jobs/TransferMonitorView.test.tsx`

1. - [ ] **Write the failing cache and outcome tests.** Create the following two test files.

   ```ts
   // web/src/features/jobs/jobMonitor.test.ts
   import { QueryClient } from '@tanstack/react-query';
   import { expect, it, vi } from 'vitest';
   import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
   import { fetchJob } from './jobApi';
   import { monitorJob, jobKey } from './jobMonitor';
   import { jobWire } from '../../test/planFixtures';
   it('refetches the canonical job for a gap and writes it into Query', async () => {
     const client = new QueryClient(); client.setQueryData(jobKey(jobWire.jobId), jobWire);
      const request = vi.fn().mockResolvedValueOnce(new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode('id: 2\ndata: {"State":"verificationfailed","RowsTransferred":9,"BytesTransferred":40}\n\n')); controller.close(); } }), { status: 200 })).mockResolvedValueOnce(new Response(JSON.stringify(jobWire), { status: 200 }));
     const monitor = monitorJob(client, jobWire as never, request, createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token'), { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() }, () => 0);
     await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); expect(client.getQueryData(jobKey(jobWire.jobId))).toMatchObject({ state: 'Running' }); monitor.close(); await monitor.done;
   });
   it('invalidates permissions on 403 and rejects a missing token before fetching a job', async () => {
     const client = new QueryClient(); client.setQueryData(['effectivePermissions'], {}); const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
     const monitor = monitorJob(client, jobWire as never, vi.fn(async () => new Response('', { status: 403 })), authentication, { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() }, () => 0);
     await monitor.done; await Promise.resolve(); expect(client.getQueryState(['effectivePermissions'])?.isInvalidated).toBe(true); await authentication.signOut(); await expect(fetchJob(jobWire.jobId, vi.fn(), authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
   });
   ```

   ```tsx
   // web/src/features/jobs/TransferMonitorView.test.tsx
   import { expect, it } from 'vitest'; import { render, screen } from '@testing-library/react';
   import { TransferMonitorView } from './TransferMonitorView'; import { jobWire } from '../../test/planFixtures';
    it('never presents verificationfailed as success', () => { render(<TransferMonitorView job={{ ...jobWire, state: 'verificationfailed' } as never} />); expect(screen.getByText(/verification failed.*not successful/i)).toBeVisible(); expect(screen.queryByText('Verification passed. Transfer succeeded.')).not.toBeInTheDocument(); });
   ```

2. - [ ] **Run the missing monitor tests.** Run `npm --prefix web test -- --run src/features/jobs/jobMonitor.test.ts src/features/jobs/TransferMonitorView.test.tsx`; expect non-zero exit and `Failed to resolve import "./jobMonitor"`.

3. - [ ] **Implement canonical job validation, Query cache binding, and outcome rendering.** Create the three files. `monitorJob` has no Zustand state and writes Query only after parsing/reduction.

   ```ts
   // web/src/features/jobs/jobApi.ts
   import type { AuthenticationAdapter } from '../../auth/authAdapter'; import { getJobUrl } from '../../api/generated/client'; import { JobResponse } from '../../api/generated/permissions.zod'; import { parseJson } from '../../api/parseJson'; import type { RequestFunction } from '../plans/planReviewApi';
   export async function fetchJob(jobId: string, request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) { const token = await authentication.getAccessToken(); if (!token) throw new Error('Not authenticated.'); return parseJson(await request(getJobUrl(jobId), { headers: { Authorization: `Bearer ${token}` }, signal }), JobResponse); }
   ```

   ```ts
   // web/src/features/jobs/jobMonitor.ts
   import type { QueryClient } from '@tanstack/react-query'; import type { AuthenticationAdapter } from '../../auth/authAdapter'; import type { RequestFunction } from '../plans/planReviewApi'; import { openJobEventStream } from './jobEventTransport'; import { fetchJob } from './jobApi'; import type { Job } from './jobReducer';
   export const jobKey = (jobId: string) => ['job', jobId] as const;
   export function monitorJob(client: QueryClient, job: Job, request: RequestFunction, authentication: AuthenticationAdapter, clock: { setTimeout: (callback: () => void, milliseconds: number) => number; clearTimeout: (id: number) => void }, random: () => number) {
     return openJobEventStream({ job, request, authentication, clock, random, onAccepted: (next) => client.setQueryData(jobKey(next.jobId), next), onGap: async (signal) => { const next = await fetchJob(job.jobId, request, authentication, signal); client.setQueryData(jobKey(next.jobId), next); return next; }, onForbidden: () => { void client.invalidateQueries({ queryKey: ['effectivePermissions'] }); }, onTerminal: () => {}, onError: () => {} });
   }
   ```

   ```tsx
   // web/src/features/jobs/TransferMonitorView.tsx
   import type { Job } from './jobReducer'; import { transferOutcome } from './jobReducer';
   export function TransferMonitorView({ job }: { job: Job }) { const outcome = transferOutcome(job.state); return <main aria-label="Transfer monitor"><h1>Transfer monitor</h1><p aria-live="polite">{outcome.text}</p><p>{job.rowsTransferred} rows transferred</p><p>{job.bytesTransferred} bytes transferred</p></main>; }
   ```

4. - [ ] **Run the monitor tests and final frontend coverage gate.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/features/jobs/jobMonitor.test.ts src/features/jobs/TransferMonitorView.test.tsx && npm --prefix web run test:coverage`; expect exit 0 and 100% statements, branches, functions, and lines.

5. - [ ] **Commit the monitor.** Run `git add web/src/features/jobs/jobApi.ts web/src/features/jobs/jobMonitor.ts web/src/features/jobs/TransferMonitorView.tsx web/src/features/jobs/jobMonitor.test.ts web/src/features/jobs/TransferMonitorView.test.tsx && git commit -m "feat: monitor verified transfer outcomes"`.

## Self-Review

- [ ] Confirm happy-dom Vitest 4 `coverage.include` reports 100% all four ways in every same-task gate; no handwritten module is deferred.
- [ ] Confirm review covers totals/bytes, mapping/grid/order, eight states, paths, risks, sealing/invalidation, export, and the `TargetSatisfied` non-refresh/upsert warning. Start is advisory; server rechecks it.
- [ ] Confirm no token enters Zustand, storage, URL, key, export, or log; jobs stay in Query. Confirm validated SSE handles watermark/gap/401/403/terminal/cleanup, and `VerificationFailed` never displays success or exceeds ADR 0002.
- [ ] Confirm `PlanReviewResponse`, `PlanInclusionPathResponse`, `StartPlanJobResponse`, `JobResponse`, `JobEventsResponse`, and all five URL helpers; defer graph, Selection Workbench, routing, and unrelated screens. Do not mistake `JobEventsResponse` for a payload validator.
