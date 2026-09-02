# DataPitcher Slice 15: Frontend Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a strict, fully covered React foundation that keeps server data, interaction state, authentication, generated transport code, and browser adapters in independently testable boundaries.

**Architecture:** TanStack Query owns every refetchable value from the server, while Zustand owns only small client interaction values behind narrow stores. OpenAPI produces both the endpoint client and Zod schemas; a thin injected-fetch adapter validates a response before a query can cache it. Pure policy and transformation modules carry behavior, and React components render accessible output without reaching directly into browser or transport APIs.

**Tech Stack:** Node 22.22.2+, npm with committed `package-lock.json`, Vite 8.2.2, React and React DOM 19.2.8, TypeScript strict mode, Zustand 5.0.15, TanStack React Query 5.102.8, Tailwind CSS and `@tailwindcss/vite` 4.3.3, Vitest and `@vitest/coverage-v8` 4.1.11, React Testing Library 16.3.3, Playwright 1.62.1, Orval 8.27.0, Zod 4.5.4, and `@vitejs/plugin-react` 6.1.1.

---

## File Structure

- `web/package.json` — npm-only scripts, exact dependency pins, and Node engine floor.
- `web/package-lock.json` — committed resolved npm dependency graph.
- `web/tsconfig.json`, `web/tsconfig.app.json`, `web/tsconfig.node.json` — strict browser and Vite TypeScript projects.
- `web/index.html` — Vite entry document.
- `web/vite.config.ts` — React, Tailwind 4, Vitest, all-files coverage, and 100% thresholds.
- `web/src/main.tsx` — tested application bootstrap.
- `web/src/styles.css` — Tailwind 4 stylesheet entry.
- `web/src/test/setup.ts` — Testing Library matcher and cleanup setup.
- `web/src/app/App.tsx` — accessible foundation shell.
- `web/src/app/App.test.tsx`, `web/src/main.test.tsx` — shell and bootstrap behavior tests.
- `web/src/app/runLabel.ts`, `web/src/app/runLabel.test.ts` — small coverage-gate sentinel.
- `scripts/test-frontend.sh` — isolated npm typecheck and frontend coverage lane.
- `web/src/stores/sessionStore.ts` — private, non-persisted session interaction store with narrow exports.
- `web/src/stores/preferencesStore.ts` — separate persisted preference store with an explicit allowlist.
- `web/src/stores/storeBoundary.test.tsx` — state ownership, persistence, and no-transport-import architecture tests.
- `web/src/auth/authAdapter.ts` — authentication adapter contract and development in-memory implementation.
- `web/src/auth/permissionPolicy.ts` — pure hide-versus-disable permission decision.
- `web/src/auth/ProtectedAction.tsx`, `web/src/auth/authAdapter.test.tsx` — accessible permission-aware rendering and adapter tests.
- `web/openapi/datapitcher.openapi.json` — checked-in OpenAPI transport source for the foundation permission response.
- `web/orval.config.ts` — reproducible generated-client and Zod-schema configuration.
- `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts` — committed generated artifacts, excluded from handwritten coverage.
- `web/src/api/parseJson.ts`, `web/src/api/parseJson.test.ts` — pure runtime response-validation boundary.
- `web/src/api/effectivePermissionsApi.ts`, `web/src/api/effectivePermissionsQuery.ts` — injected-fetch shell and Query option factory.
- `web/src/app/AppProviders.tsx`, `web/src/api/effectivePermissionsQuery.test.tsx` — thin Query client shell and server-state tests.
- `web/src/app/ciContract.test.ts` — local assertion that CI invokes the separate frontend lane and generated-artifact drift check.
- `.github/workflows/ci.yml` — distinct backend and frontend CI jobs.

## Scope and Deferrals

This slice deliberately creates no dependency graph, Selection Workbench, plan-review screen, or transfer monitor. Those are independent product-sized slices: the graph needs worker layout and large-subgraph performance work, selection needs its query language and preview rules, plan review needs immutable plan semantics, and transfer monitoring needs validated SSE/reconnect behavior. A cosmetic fragment of any of them would hide rather than reduce their risk.

The foundation is worth completing first because its state boundary, generated transport client, authentication adapter, and coverage lane decide whether every later feature can achieve 100% handwritten coverage. Query-owned topology, SSE job state, table payloads, graph layout results, and any future preview rows must not be copied into Zustand. Likewise, worker, fetch, timer, scheduler, and query-client calls must remain behind small adapters when those features arrive. This slice establishes the enforceable pattern, not a half-built workflow screen.

Vite 8 requires Node 20.19+ or 22.12+; this repository sets Node 22.22.2+ because the pinned generator and JSDOM test runtime have stricter engine requirements. Use npm only, commit `package-lock.json`, and never use a globally installed generator. Tailwind 4 requires `@tailwindcss/vite`; do not recycle a Tailwind 3 PostCSS/config-file setup. Playwright is pinned now for later browser smoke tests but does not enter the unit coverage calculation.

The coverage lane is intentionally separate from `scripts/test-all.sh`. The existing aggregate gate measures .NET build and Coverlet/ReportGenerator output, while the frontend lane measures TypeScript with Vitest's V8 provider; neither toolchain can honestly merge the other's counters. The frontend gate uses `all: true`, explicit first-party `src/**/*.{ts,tsx}` inclusion, and 100% statement, branch, function, and line thresholds. Generated files are the sole source exclusion: they are verified through deterministic regeneration and boundary integration instead of pretending vendor generator output is handwritten code.

No test may depend on a real network or elapsed time. Tests supply a request function, token adapter, `AbortSignal`, clock, and scheduler whenever a module needs one. Prefer Testing Library queries by accessible role and name, never test identifiers. Every module introduced by a task has an executable test in that same task; an uncovered branch or unimported source file blocks merge.

### Task 1: Scaffold the strict Vite application

**Files:**
- Create: `web/package.json`, `web/package-lock.json`, `web/tsconfig.json`, `web/tsconfig.app.json`, `web/tsconfig.node.json`, `web/index.html`, `web/vite.config.ts`, `web/src/test/setup.ts`, `web/src/app/App.tsx`, `web/src/app/App.test.tsx`, `web/src/main.tsx`, `web/src/main.test.tsx`, `web/src/styles.css`
- Modify: none
- Test: `web/src/app/App.test.tsx`, `web/src/main.test.tsx`

1. - [ ] **Write the failing foundation tests and their runnable npm/Vite configuration.** Put the following complete tests in the two test files; they intentionally import application modules that do not yet exist.

   ```tsx
   // web/src/app/App.test.tsx
   import { expect, it } from 'vitest';
   import { render, screen } from '@testing-library/react';
   import { App } from './App';

   it('renders the application landmark and name', () => {
     render(<App />);
     expect(screen.getByRole('main')).toBeVisible();
     expect(screen.getByRole('heading', { name: 'DataPitcher' })).toBeVisible();
   });

   // web/src/main.test.tsx
   import { afterEach, beforeEach, expect, it, vi } from 'vitest';

   const render = vi.fn();
   vi.mock('react-dom/client', () => ({ createRoot: vi.fn(() => ({ render })) }));

   beforeEach(() => {
     document.body.innerHTML = '<div id="root"></div>';
   });
   afterEach(() => {
     vi.clearAllMocks();
     vi.resetModules();
   });

   it('mounts the application into the Vite root', async () => {
     await import('./main');
     expect(render).toHaveBeenCalledOnce();
   });
   ```

   Create this complete package manifest; `package-lock.json` is its `npm install` result. Define no generated-client script yet because Task 5 creates its configuration and then adds that script.

   ```json
   {
     "name": "datapitcher-web", "private": true, "version": "0.0.0", "type": "module",
     "engines": { "node": ">=22.22.2" },
     "scripts": { "dev": "vite", "build": "tsc -b && vite build", "typecheck": "tsc -b", "test": "vitest", "test:coverage": "vitest run --coverage" },
     "dependencies": { "@tanstack/react-query": "5.102.8", "react": "19.2.8", "react-dom": "19.2.8", "zod": "4.5.4", "zustand": "5.0.15" },
     "devDependencies": { "@tailwindcss/vite": "4.3.3", "@testing-library/jest-dom": "7.0.1", "@testing-library/react": "16.3.3", "@types/react": "19.2.18", "@types/react-dom": "19.2.5", "@vitejs/plugin-react": "6.1.1", "@vitest/coverage-v8": "4.1.11", "jsdom": "30.0.1", "orval": "8.27.0", "playwright": "1.62.1", "tailwindcss": "4.3.3", "typescript": "6.0.3", "vite": "8.2.2", "vitest": "4.1.11" }
   }
   ```

   Configure strict TypeScript with `strict`, `noUncheckedIndexedAccess`, `noImplicitOverride`, and `verbatimModuleSyntax`; use `react-jsx`, bundler module resolution, and separate app/node project references. Configure Vite with its React plugin, `tailwindcss()` from `@tailwindcss/vite`, JSDOM Vitest setup, and this complete coverage block:

   ```ts
   import { defineConfig } from 'vitest/config';
   import react from '@vitejs/plugin-react';
   import tailwindcss from '@tailwindcss/vite';

   export default defineConfig({
     plugins: [react(), tailwindcss()],
     test: {
       environment: 'jsdom', setupFiles: ['./src/test/setup.ts'],
       coverage: {
         provider: 'v8', all: true, include: ['src/**/*.{ts,tsx}'],
         exclude: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'src/api/generated/**'],
         thresholds: { statements: 100, branches: 100, functions: 100, lines: 100 },
       },
     },
   });
   ```

2. - [ ] **Run the missing-module tests.** Run `npm --prefix web install && npm --prefix web test -- --run src/app/App.test.tsx src/main.test.tsx`; expect non-zero exit and Vite's `Failed to resolve import "./App" from "src/app/App.test.tsx"` message.

3. - [ ] **Implement the smallest accessible application shell and bootstrap.** Create `web/src/app/App.tsx` as the rendering-only component below, `web/src/main.tsx` as the tested root bootstrap, and `web/src/styles.css` as the Tailwind 4 entry. Do not add a router, feature state, API call, or placeholder screen.

   ```tsx
   // web/src/app/App.tsx
   export function App() {
     return <main><h1>DataPitcher</h1><p>Transfer planning workspace.</p></main>;
   }

   // web/src/main.tsx
   import { StrictMode } from 'react';
   import { createRoot } from 'react-dom/client';
   import { App } from './app/App';
   import './styles.css';

   createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>);
   ```

   ```css
   /* web/src/styles.css */
   @import "tailwindcss";
   ```

   Use `import '@testing-library/jest-dom/vitest'` in `web/src/test/setup.ts`, include `<div id="root"></div><script type="module" src="/src/main.tsx"></script>` in `web/index.html`, and generate the committed lockfile from the same `npm install` command. Tailwind has no Tailwind 3 config file because the Vite plugin owns the Tailwind 4 integration.

4. - [ ] **Run the scaffold typecheck and tests.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run`; expect exit 0 with `2 passed`.

5. - [ ] **Commit the runnable scaffold.** Run `git add web/package.json web/package-lock.json web/tsconfig.json web/tsconfig.app.json web/tsconfig.node.json web/index.html web/vite.config.ts web/src/test/setup.ts web/src/app/App.tsx web/src/app/App.test.tsx web/src/main.tsx web/src/main.test.tsx web/src/styles.css && git commit -m "feat: scaffold frontend foundation"`.

### Task 2: Enforce and demonstrate the frontend coverage gate

**Files:**
- Create: `web/src/app/runLabel.ts`, `web/src/app/runLabel.test.ts`, `scripts/test-frontend.sh`
- Modify: none
- Test: `web/src/app/runLabel.test.ts`

1. - [ ] **Write the failing coverage-sentinel test.** Create `web/src/app/runLabel.test.ts` with this complete test.

   ```ts
   import { expect, it } from 'vitest';
   import { frontendLaneLabel } from './runLabel';

   it('names the isolated frontend test lane', () => {
     expect(frontendLaneLabel).toBe('frontend');
   });
   ```

2. - [ ] **Run the sentinel test before its module exists.** Run `npm --prefix web test -- --run src/app/runLabel.test.ts`; expect non-zero exit and `Failed to resolve import "./runLabel"`.

3. - [ ] **Implement the sentinel and the isolated lane.** Create `web/src/app/runLabel.ts` with `export const frontendLaneLabel = 'frontend';`. Create executable `scripts/test-frontend.sh` with `set -euo pipefail`, then exactly `npm --prefix web ci`, `npm --prefix web run typecheck`, and `npm --prefix web run test:coverage` on separate lines. This script is the frontend gate; do not append it to the .NET aggregate script because the counters and reports are from different toolchains.

4. - [ ] **Run the isolated gate.** Run `scripts/test-frontend.sh`; expect exit 0 and Vitest reports 100% for statements, branches, functions, and lines.

5. - [ ] **Temporarily delete the only sentinel test.** Run `mv web/src/app/runLabel.test.ts web/src/app/runLabel.test.ts.disabled`.

6. - [ ] **Prove that the all-files gate fails.** Run `scripts/test-frontend.sh`; expect non-zero exit with Vitest's `ERROR: Coverage for lines (0%) does not meet global threshold (100%)` for `runLabel.ts`. This deliberate red result proves the gate bites; a gate never observed to fail is not a gate.

7. - [ ] **Restore the deleted sentinel test.** Run `mv web/src/app/runLabel.test.ts.disabled web/src/app/runLabel.test.ts`.

8. - [ ] **Re-run the restored coverage gate.** Run `scripts/test-frontend.sh`; expect exit 0 and four 100% totals.

9. - [ ] **Commit the independent coverage lane.** Run `git add web/src/app/runLabel.ts web/src/app/runLabel.test.ts scripts/test-frontend.sh && git commit -m "test: gate frontend coverage"`.

### Task 3: Establish structural session and preference state boundaries

Zustand selectors must return primitives or use an explicit shallow comparator. Never return a newly allocated array or object from a selector without shallow comparison: React will treat every selection as changed and can loop until the maximum update depth is exceeded.

**Files:**
- Create: `web/src/stores/sessionStore.ts`, `web/src/stores/preferencesStore.ts`, `web/src/stores/storeBoundary.test.tsx`
- Modify: none
- Test: `web/src/stores/storeBoundary.test.tsx`

1. - [ ] **Write the failing state-boundary tests.** Create `web/src/stores/storeBoundary.test.tsx` with this complete test body. It makes the persisted shape and forbidden transport-import rule executable rather than review convention.

   ```tsx
    import { readFileSync } from 'node:fs';
    import { resolve } from 'node:path';
    import { afterEach, expect, it } from 'vitest';
    import { render, screen } from '@testing-library/react';
    import { createPreferencesStore, preferenceActions } from './preferencesStore';
    import { sessionActions, useSessionIdentity, useSourceConnectionId, useTargetConnectionId } from './sessionStore';

    function SessionProbe() {
      const identity = useSessionIdentity();
      const sourceId = useSourceConnectionId();
      const targetId = useTargetConnectionId();
      return <output role="status">{`${identity?.subjectId}|${sourceId}|${targetId}`}</output>;
    }
    function PreferenceProbe({ preferences }: { preferences: ReturnType<typeof createPreferencesStore> }) {
      return <output role="status">{`${preferences.useColorScheme()}|${preferences.useReducedMotion()}`}</output>;
    }

   afterEach(() => {
     sessionActions.setIdentity(null);
     sessionActions.setConnectionIds(null, null);
     preferenceActions.setColorScheme('system');
     preferenceActions.setReducedMotion(false);
    });

   it('exposes only named session identifiers through selectors', () => {
     sessionActions.setIdentity({ subjectId: 'operator-1', tenantId: 'tenant-1' });
     sessionActions.setConnectionIds('source-1', 'target-1');
     render(<SessionProbe />);
     expect(screen.getByRole('status')).toHaveTextContent('operator-1|source-1|target-1');
   });

    it('persists only the preference allowlist', () => {
      const values = new Map<string, string>();
      const preferences = createPreferencesStore({
        getItem: (name) => values.get(name) ?? null,
        setItem: (name, value) => { values.set(name, value); },
        removeItem: (name) => { values.delete(name); },
      });
      preferences.actions.setColorScheme('dark');
      render(<PreferenceProbe preferences={preferences} />);
      expect(screen.getByRole('status')).toHaveTextContent('dark|false');
      expect(JSON.parse(values.get('datapitcher.preferences')!).state)
        .toEqual({ colorScheme: 'dark', reducedMotion: false });
    });

    it('keeps store modules free of transport imports and preferences allowlisted', () => {
      const stores = resolve(process.cwd(), 'src/stores');
      const preferenceSource = readFileSync(resolve(stores, 'preferencesStore.ts'), 'utf8');
      expect(preferenceSource).toContain('partialize');
      for (const name of ['sessionStore.ts', 'preferencesStore.ts']) {
        expect(readFileSync(resolve(stores, name), 'utf8')).not.toMatch(/from\s+['"][^'"]*\/api\//);
      }
    });
   ```

2. - [ ] **Run the missing-store tests.** Run `npm --prefix web test -- --run src/stores/storeBoundary.test.tsx`; expect non-zero exit and `Failed to resolve import "./sessionStore"`.

3. - [ ] **Implement two deliberately narrow Zustand stores.** Create the following session store; it holds identifiers and a small identity value object only, is not persisted, does not export its underlying Zustand hook, and has neither a generic object bag nor arbitrary patch action.

   ```ts
   import { create } from 'zustand';

   export type SessionIdentity = Readonly<{ subjectId: string; tenantId: string }>;
   type SessionState = {
     identity: SessionIdentity | null;
     sourceConnectionId: string | null;
     targetConnectionId: string | null;
     setIdentity: (identity: SessionIdentity | null) => void;
     setConnectionIds: (sourceConnectionId: string | null, targetConnectionId: string | null) => void;
   };

   const useSessionState = create<SessionState>()((set) => ({
     identity: null,
     sourceConnectionId: null,
     targetConnectionId: null,
     setIdentity: (identity) => set({ identity }),
     setConnectionIds: (sourceConnectionId, targetConnectionId) => set({ sourceConnectionId, targetConnectionId }),
   }));

   export const sessionActions = {
     setIdentity: (identity: SessionIdentity | null) => useSessionState.getState().setIdentity(identity),
     setConnectionIds: (sourceConnectionId: string | null, targetConnectionId: string | null) => useSessionState.getState().setConnectionIds(sourceConnectionId, targetConnectionId),
   };
    export const useSessionIdentity = () => useSessionState((state) => state.identity);
    export const useSourceConnectionId = () => useSessionState((state) => state.sourceConnectionId);
    export const useTargetConnectionId = () => useSessionState((state) => state.targetConnectionId);
   ```

   Create the separately persisted preference store below. It exports only named setters and selectors. `partialize` stays even though these are currently the only data fields: omitting it persists every future state field by default, exactly the accident this boundary prevents.

   ```ts
   import { create } from 'zustand';
    import { createJSONStorage, persist, type StateStorage } from 'zustand/middleware';

   type ColorScheme = 'system' | 'light' | 'dark';
   type PreferencesState = {
     colorScheme: ColorScheme;
     reducedMotion: boolean;
     setColorScheme: (colorScheme: ColorScheme) => void;
     setReducedMotion: (reducedMotion: boolean) => void;
   };

    export function createPreferencesStore(storage: StateStorage = window.localStorage) {
      const usePreferencesState = create<PreferencesState>()(persist(
        (set) => ({
          colorScheme: 'system',
          reducedMotion: false,
          setColorScheme: (colorScheme) => set({ colorScheme }),
          setReducedMotion: (reducedMotion) => set({ reducedMotion }),
        }),
        {
          name: 'datapitcher.preferences',
          storage: createJSONStorage(() => storage),
          partialize: ({ colorScheme, reducedMotion }) => ({ colorScheme, reducedMotion }),
        },
      ));
      return {
        actions: {
          setColorScheme: (colorScheme: ColorScheme) => usePreferencesState.getState().setColorScheme(colorScheme),
          setReducedMotion: (reducedMotion: boolean) => usePreferencesState.getState().setReducedMotion(reducedMotion),
        },
        useColorScheme: () => usePreferencesState((state) => state.colorScheme),
        useReducedMotion: () => usePreferencesState((state) => state.reducedMotion),
      };
    }

    const preferences = createPreferencesStore();
    export const preferenceActions = preferences.actions;
    export const useColorScheme = preferences.useColorScheme;
    export const useReducedMotion = preferences.useReducedMotion;
    ```

    `createJSONStorage` is the explicit persistence adapter. Its production instance receives `window.localStorage`, never the ambient `localStorage` global; the test injects an in-memory `StateStorage` double. The factory result still exposes only named actions and selectors, never the underlying Zustand hook or a generic patch operation.

   Neither store may import generated types or any other transport type; that makes large server payloads unstorable by construction. Access tokens are not a session field, not a preference, never in localStorage, and never in a URL.

4. - [ ] **Run the state-boundary tests.** Run `npm --prefix web test -- --run src/stores/storeBoundary.test.tsx`; expect exit 0 with all three state ownership assertions passing.

5. - [ ] **Commit the structurally constrained stores.** Run `git add web/src/stores/sessionStore.ts web/src/stores/preferencesStore.ts web/src/stores/storeBoundary.test.tsx && git commit -m "feat: separate frontend state ownership"`.

### Task 4: Add the authentication adapter and permission-aware rendering rule

**Files:**
- Create: `web/src/auth/authAdapter.ts`, `web/src/auth/permissionPolicy.ts`, `web/src/auth/ProtectedAction.tsx`, `web/src/auth/authAdapter.test.tsx`
- Modify: none
- Test: `web/src/auth/authAdapter.test.tsx`

1. - [ ] **Write the failing authentication and control-state tests.** Create `web/src/auth/authAdapter.test.tsx` with this complete test body. It uses accessible role/name assertions rather than test identifiers.

   ```tsx
   import { expect, it } from 'vitest';
   import { render, screen } from '@testing-library/react';
   import { createDevelopmentAuthenticationAdapter } from './authAdapter';
   import { ProtectedAction } from './ProtectedAction';

   it('keeps a development token in the adapter closure until sign-out', async () => {
     const adapter = createDevelopmentAuthenticationAdapter(
       { subjectId: 'operator-1', tenantId: 'tenant-1' }, 'development-token',
     );
     await expect(adapter.getAccessToken()).resolves.toBe('development-token');
     await adapter.signOut();
     await expect(adapter.getPrincipal()).resolves.toBeNull();
     await expect(adapter.getAccessToken()).resolves.toBeNull();
   });

   it('hides denied actions and disables permitted actions with unmet prerequisites', () => {
     const { rerender } = render(
       <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set()} prerequisiteMet={false} reason="Plan must be sealed">
         Start transfer
       </ProtectedAction>,
     );
     expect(screen.queryByRole('button', { name: 'Start transfer' })).not.toBeInTheDocument();
     rerender(
       <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set(['Transfers.Start'])} prerequisiteMet={false} reason="Plan must be sealed">
         Start transfer
       </ProtectedAction>,
     );
     expect(screen.getByRole('button', { name: 'Start transfer' })).toBeDisabled();
     expect(screen.getByText('Plan must be sealed')).toBeVisible();
     rerender(
       <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set(['Transfers.Start'])} prerequisiteMet={true} reason="Plan must be sealed">
         Start transfer
       </ProtectedAction>,
     );
     expect(screen.getByRole('button', { name: 'Start transfer' })).toBeEnabled();
   });
   ```

2. - [ ] **Run the missing-adapter tests.** Run `npm --prefix web test -- --run src/auth/authAdapter.test.tsx`; expect non-zero exit and `Failed to resolve import "./authAdapter"`.

3. - [ ] **Implement the closure-held token adapter, pure policy, and rendering-only control.** Define `AuthenticatedPrincipal` as a small `{ subjectId, tenantId }` value object and this narrow interface:

   ```ts
   export type AuthenticatedPrincipal = Readonly<{ subjectId: string; tenantId: string }>;

   export interface AuthenticationAdapter {
     getPrincipal(): Promise<AuthenticatedPrincipal | null>;
     getAccessToken(): Promise<string | null>;
     signOut(): Promise<void>;
   }

   export function createDevelopmentAuthenticationAdapter(
     principal: AuthenticatedPrincipal,
     token: string,
   ): AuthenticationAdapter {
     let activePrincipal: AuthenticatedPrincipal | null = principal;
     let activeToken: string | null = token;
     return {
       getPrincipal: async () => activePrincipal,
       getAccessToken: async () => activeToken,
       signOut: async () => { activePrincipal = null; activeToken = null; },
     };
   }
   ```

   Keep `activeToken` exclusively in this provider closure. Do not add a token field to either Zustand store, localStorage, sessionStorage, query keys, route state, or a URL.

   Implement `permissionPolicy.ts` and `ProtectedAction.tsx` as follows. Permission denial hides a control; prerequisite, validation, or busy status disables an otherwise permitted control and explains why. Frontend checks are user experience only: every protected action must still be authorized by the server against the current principal and resource at execution time.

   ```tsx
   // web/src/auth/permissionPolicy.ts
   export type ControlState = { visible: boolean; disabled: boolean; reason?: string };
   export function controlState(requiredPermission: string, grantedPermissions: ReadonlySet<string>, prerequisiteMet: boolean, reason: string): ControlState {
     if (!grantedPermissions.has(requiredPermission)) return { visible: false, disabled: false };
     return prerequisiteMet ? { visible: true, disabled: false } : { visible: true, disabled: true, reason };
   }

   // web/src/auth/ProtectedAction.tsx
   import type { ReactNode } from 'react';
   import { controlState } from './permissionPolicy';

   type ProtectedActionProps = { requiredPermission: string; grantedPermissions: ReadonlySet<string>; prerequisiteMet: boolean; reason: string; children: ReactNode };
   export function ProtectedAction(props: ProtectedActionProps) {
     const state = controlState(props.requiredPermission, props.grantedPermissions, props.prerequisiteMet, props.reason);
     if (!state.visible) return null;
     return <><button disabled={state.disabled}>{props.children}</button>{state.reason && <p>{state.reason}</p>}</>;
   }
   ```

4. - [ ] **Run the adapter and accessible-control tests.** Run `npm --prefix web test -- --run src/auth/authAdapter.test.tsx`; expect exit 0 with the token-clear, hidden-control, and disabled-control assertions passing.

5. - [ ] **Commit the authentication seam and UI policy.** Run `git add web/src/auth/authAdapter.ts web/src/auth/permissionPolicy.ts web/src/auth/ProtectedAction.tsx web/src/auth/authAdapter.test.tsx && git commit -m "feat: add frontend authentication adapter"`.

### Task 5: Generate the OpenAPI client and Zod schemas from one contract

**Files:**
- Create: `web/openapi/datapitcher.openapi.json`, `web/orval.config.ts`, `web/src/api/generated/client.ts`, `web/src/api/generated/permissions.zod.ts`, `web/src/api/parseJson.ts`, `web/src/api/parseJson.test.ts`
- Modify: `web/package.json`
- Test: `web/src/api/parseJson.test.ts`

1. - [ ] **Write the failing runtime-validation tests.** Create `web/src/api/parseJson.test.ts` with this complete test body. It imports the not-yet-generated schema and uses only in-memory `Response` values.

   ```ts
   import { expect, it } from 'vitest';
   import { ZodError } from 'zod';
   import { EffectivePermissionsSchema } from './generated/permissions.zod';
   import { parseJson } from './parseJson';

   it('accepts a response matching the generated schema', async () => {
     const response = new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] }), { status: 200 });
     await expect(parseJson(response, EffectivePermissionsSchema)).resolves.toEqual({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] });
   });

   it('rejects a malformed response before application state can receive it', async () => {
     const response = new Response(JSON.stringify({ permissions: ['Transfers.Start'] }), { status: 200 });
     await expect(parseJson(response, EffectivePermissionsSchema)).rejects.toBeInstanceOf(ZodError);
   });

   it('rejects an unsuccessful HTTP response before parsing', async () => {
     const response = new Response('', { status: 500 });
     await expect(parseJson(response, EffectivePermissionsSchema)).rejects.toThrow('Request failed: 500');
   });
   ```

2. - [ ] **Run the missing-generated-schema tests.** Run `npm --prefix web test -- --run src/api/parseJson.test.ts`; expect non-zero exit and `Failed to resolve import "./generated/permissions.zod"`.

3. - [ ] **Define the single contract, generator, and validation boundary.** Add this valid OpenAPI 3.1 document, then add only the `generate:api` script of `orval --config orval.config.ts` to the already-pinned package manifest. Update the lockfile only through `npm --prefix web install`.

   ```json
   {
     "openapi": "3.1.0", "info": { "title": "DataPitcher API", "version": "1.0.0" },
     "paths": { "/api/auth/effective-permissions": { "get": { "operationId": "effectivePermissions", "responses": { "200": { "description": "Effective permission set", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/EffectivePermissions" } } } } } } } },
     "components": { "schemas": { "EffectivePermissions": { "type": "object", "required": ["principalId", "tenantId", "permissions"], "properties": { "principalId": { "type": "string", "minLength": 1 }, "tenantId": { "type": "string", "minLength": 1 }, "permissions": { "type": "array", "items": { "type": "string" } } } } } }
   }
   ```

   Configure two named Orval targets from the same local OpenAPI document: a fetch client written to `src/api/generated/client.ts` and a Zod target written to `src/api/generated/permissions.zod.ts` with `client: 'zod'`. The client target produces `getEffectivePermissionsUrl`; the Zod target produces `EffectivePermissionsSchema` and its inferred type. The exact configuration is deliberately two inputs pointing at the one contract, never two handwritten field lists. Generated artifacts are committed, are excluded from Vitest coverage, and must never be manually edited.

   ```ts
   import { defineConfig } from 'orval';

   export default defineConfig({
     client: {
       input: { target: './openapi/datapitcher.openapi.json' },
       output: { target: './src/api/generated/client.ts', client: 'fetch', mode: 'single' },
     },
     validation: {
       input: { target: './openapi/datapitcher.openapi.json' },
       output: { target: './src/api/generated/permissions.zod.ts', client: 'zod', mode: 'single' },
     },
   });
   ```

   Implement the handwritten boundary as follows; it validates the actual wire value because TypeScript types disappear at runtime.

   ```ts
   import { z } from 'zod';

   export async function parseJson<T>(response: Response, schema: z.ZodType<T>): Promise<T> {
     if (!response.ok) throw new Error(`Request failed: ${response.status}`);
     return schema.parse(await response.json());
   }
   ```

   The generated `EffectivePermissionsSchema`, not a copied interface, is the schema argument. Handwritten refinements may wrap a generated schema but may not restate transport fields.

4. - [ ] **Generate, typecheck, and run the validation tests.** Run `npm --prefix web run generate:api && npm --prefix web run typecheck && npm --prefix web test -- --run src/api/parseJson.test.ts`; expect exit 0 and both valid-response and invalid-response assertions passing.

5. - [ ] **Commit the contract and generated artifacts.** Run `git add web/package.json web/openapi/datapitcher.openapi.json web/orval.config.ts web/src/api/generated web/src/api/parseJson.ts web/src/api/parseJson.test.ts && git commit -m "feat: generate frontend API boundary"`.

### Task 6: Put validated permission data in Query, not Zustand

**Files:**
- Create: `web/src/api/effectivePermissionsApi.ts`, `web/src/api/effectivePermissionsQuery.ts`, `web/src/app/AppProviders.tsx`, `web/src/api/effectivePermissionsQuery.test.tsx`
- Modify: `web/src/main.tsx`
- Test: `web/src/api/effectivePermissionsQuery.test.tsx`

1. - [ ] **Write the failing Query-boundary tests.** Create `web/src/api/effectivePermissionsQuery.test.tsx` with this complete test body. The injected request has no network or timing dependency, and the Query options turn retries off so malformed input cannot schedule a retry.

   ```ts
   import { QueryClient, useQueryClient } from '@tanstack/react-query';
   import { expect, it, vi } from 'vitest';
   import { render, screen } from '@testing-library/react';
   import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
   import { AppProviders } from '../app/AppProviders';
   import { getEffectivePermissionsUrl } from './generated/client';
   import { fetchEffectivePermissions } from './effectivePermissionsApi';
   import { effectivePermissionsQueryOptions } from './effectivePermissionsQuery';

   const principal = { subjectId: 'operator-1', tenantId: 'tenant-1' };
   let observedClient: QueryClient | undefined;
   function QueryClientProbe() {
     observedClient = useQueryClient();
     return <output role="status">query-ready</output>;
   }

   it('validates injected-fetch data before Query resolves it', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] }), { status: 200 }));
     const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
     const client = new QueryClient();
     await expect(client.fetchQuery(effectivePermissionsQueryOptions(principal, request, authentication)))
       .resolves.toEqual({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] });
     expect(request).toHaveBeenCalledWith(getEffectivePermissionsUrl(), expect.objectContaining({ headers: { Authorization: 'Bearer development-token' } }));
   });

   it('rejects malformed data instead of putting it in Query', async () => {
     const request = vi.fn(async () => new Response(JSON.stringify({ permissions: [] }), { status: 200 }));
     const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
     await expect(new QueryClient().fetchQuery(effectivePermissionsQueryOptions(principal, request, authentication))).rejects.toThrow();
   });

   it('rejects an absent token and retains an injected or default Query client', async () => {
     const authentication = createDevelopmentAuthenticationAdapter(principal, 'development-token');
     await authentication.signOut();
     await expect(fetchEffectivePermissions(vi.fn(), authentication, new AbortController().signal)).rejects.toThrow('Not authenticated.');
     const injected = new QueryClient();
     const { unmount } = render(<AppProviders client={injected}><QueryClientProbe /></AppProviders>);
     expect(screen.getByRole('status')).toHaveTextContent('query-ready');
     expect(observedClient).toBe(injected);
     unmount();
     render(<AppProviders><QueryClientProbe /></AppProviders>);
     expect(observedClient).toBeInstanceOf(QueryClient);
   });
   ```

2. - [ ] **Run the missing-Query-module tests.** Run `npm --prefix web test -- --run src/api/effectivePermissionsQuery.test.tsx`; expect non-zero exit and `Failed to resolve import "./effectivePermissionsQuery"`.

3. - [ ] **Implement the injected-fetch adapter, Query option factory, and provider shell.** The following complete modules establish the only fetch and Query-client calls. The URL and response type come from generated artifacts; no endpoint string or transport interface is handwritten.

   ```ts
   // web/src/api/effectivePermissionsApi.ts
   import type { AuthenticationAdapter } from '../auth/authAdapter';
   import { getEffectivePermissionsUrl } from './generated/client';
   import { EffectivePermissionsSchema } from './generated/permissions.zod';
   import { parseJson } from './parseJson';

   export type RequestFunction = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
   export async function fetchEffectivePermissions(request: RequestFunction, authentication: AuthenticationAdapter, signal: AbortSignal) {
     const token = await authentication.getAccessToken();
     if (!token) throw new Error('Not authenticated.');
     const response = await request(getEffectivePermissionsUrl(), { headers: { Authorization: `Bearer ${token}` }, signal });
     return parseJson(response, EffectivePermissionsSchema);
   }

   // web/src/api/effectivePermissionsQuery.ts
   import type { AuthenticatedPrincipal, AuthenticationAdapter } from '../auth/authAdapter';
   import { fetchEffectivePermissions, type RequestFunction } from './effectivePermissionsApi';

   export function effectivePermissionsQueryOptions(principal: AuthenticatedPrincipal, request: RequestFunction, authentication: AuthenticationAdapter) {
     return {
       queryKey: ['effectivePermissions', principal.subjectId, principal.tenantId] as const,
       staleTime: 30_000,
       retry: false,
       queryFn: ({ signal }: { signal: AbortSignal }) => fetchEffectivePermissions(request, authentication, signal),
     };
   }
   ```

   ```tsx
   // web/src/app/AppProviders.tsx
   import { useState, type ReactNode } from 'react';
   import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

   export function AppProviders({ children, client }: { children: ReactNode; client?: QueryClient }) {
     const [queryClient] = useState(() => client ?? new QueryClient());
     return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
   }
   ```

   Modify `main.tsx` to wrap `<App />` in `<AppProviders>`. This is server state: it belongs in TanStack Query for freshness and invalidation, never in Zustand. A future role change, tenant change, logout, or 403 invalidates this key rather than copying permissions into a store. Components may consume Query data through hooks, but they do not call `fetch`, `QueryClient`, `Worker`, timers, or storage directly.

4. - [ ] **Run the injected server-state tests.** Run `npm --prefix web run typecheck && npm --prefix web test -- --run src/api/effectivePermissionsQuery.test.tsx`; expect exit 0 with the validated-cache-input and malformed-response assertions passing.

5. - [ ] **Commit the server-state adapter boundary.** Run `git add web/src/api/effectivePermissionsApi.ts web/src/api/effectivePermissionsQuery.ts web/src/app/AppProviders.tsx web/src/api/effectivePermissionsQuery.test.tsx web/src/main.tsx && git commit -m "feat: add query-backed permissions"`.

### Task 7: Add CI drift enforcement and complete the foundation review

**Files:**
- Create: `web/src/app/ciContract.test.ts`, `.github/workflows/ci.yml`
- Modify: none
- Test: `web/src/app/ciContract.test.ts`

1. - [ ] **Write the failing CI-contract test.** Create `web/src/app/ciContract.test.ts` with this complete test. The absent workflow is the intended first failure.

   ```ts
   import { readFileSync } from 'node:fs';
   import { expect, it } from 'vitest';

   it('keeps backend, frontend, and generation checks explicit in CI', () => {
     const workflow = readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8');
     expect(workflow).toContain('./scripts/test-all.sh');
     expect(workflow).toContain('./scripts/test-frontend.sh');
     expect(workflow).toContain('npm --prefix web run generate:api');
     expect(workflow).toContain('git diff --exit-code -- web/src/api/generated');
   });
   ```

2. - [ ] **Run the missing-workflow contract test.** Run `npm --prefix web test -- --run src/app/ciContract.test.ts`; expect non-zero exit and an `ENOENT` message for `.github/workflows/ci.yml`.

3. - [ ] **Implement the separate CI jobs.** Create this workflow. Regeneration is local against the committed OpenAPI document, so the drift check neither calls a service nor weakens the no-network test rule. Keeping jobs separate preserves meaningful toolchain-specific failure output and thresholds.

   ```yaml
   name: ci
   on: [push, pull_request]
   jobs:
     backend:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - uses: actions/setup-dotnet@v4
           with: { dotnet-version: 10.0.400 }
         - run: ./scripts/test-all.sh
     frontend:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - uses: actions/setup-node@v4
           with: { node-version: 22.22.2, cache: npm, cache-dependency-path: web/package-lock.json }
         - run: npm --prefix web ci
         - run: npm --prefix web run generate:api
         - run: git diff --exit-code -- web/src/api/generated
         - run: ./scripts/test-frontend.sh
   ```

4. - [ ] **Run the CI-contract test and complete the local gate.** Run `npm --prefix web test -- --run src/app/ciContract.test.ts && scripts/test-frontend.sh`; expect exit 0, the CI-contract assertions to pass, and all handwritten frontend coverage totals to remain 100%.

5. - [ ] **Commit the CI enforcement.** Run `git add web/src/app/ciContract.test.ts .github/workflows/ci.yml && git commit -m "ci: verify frontend generation and coverage"`.

## Self-Review

- [ ] Confirm `scripts/test-frontend.sh` runs the strict TypeScript check and Vitest V8 all-files coverage with 100% statements, branches, functions, and lines. Confirm the deliberate test deletion in Task 2 produced a non-zero exit before restoration; do not claim a gate works without that observed red run.
- [ ] Confirm the backend aggregate gate and frontend gate remain distinct in local scripts and CI because Coverlet/ReportGenerator and Vitest V8 measure different toolchains. Confirm the only coverage exclusion is generated API output and that generated output is instead covered by regeneration plus a runtime-boundary test.
- [ ] Confirm session Zustand state is private and non-persisted, contains only identifiers and small values, exports no arbitrary patch action, and imports no transport type. Confirm preferences use a separate persisted store with an explicit `partialize` allowlist. Confirm no access token appears in any store, browser storage, URL, query key, or log.
- [ ] Confirm every server payload is parsed with a generated Zod schema before the Query function resolves, that OpenAPI is the single input for generated client and schemas, and that CI regeneration fails on drift. Confirm permission denial hides controls, prerequisites disable permitted controls with a reason, and the document states server-side authorization remains authoritative.
- [ ] Confirm pure modules contain policy and parsing; rendering components contain only accessibility and presentation; thin adapter shells own fetch and Query-client integration. For future Workers, timers, clocks, schedulers, SSE, Monaco, React Flow, and layout, add injected seams before behavior, not after coverage fails. Reaching 100% honestly costs roughly 30–50% additional effort and requires these seams; coverage demonstrates executed handwritten paths, not performance or security.
- [ ] Confirm this plan defers the dependency graph, Selection Workbench, plan review, and transfer monitor in full. Verify no task introduces their state, routes, screens, worker code, timers, or network behavior.
- [ ] Re-read every TypeScript and TSX fragment for strict-mode coherence: every imported symbol is defined in the same or an earlier task, every declared type is used, generated names are consistently `EffectivePermissionsSchema`, `getEffectivePermissionsUrl`, and `effectivePermissionsQueryOptions`, and no task references a file created by a later task. Ensure each module has a same-task test and every test controls fetch, time, and scheduling dependencies.
