import { useStore } from 'zustand';
import { createStore, type StoreApi } from 'zustand/vanilla';
import { createJSONStorage, persist } from 'zustand/middleware';
import { resolveStorage } from './persistence';

/**
 * The API exposes no "list plans" endpoint and saved selections carry no display name, so the client keeps a small
 * local registry of names and identifiers it created. It holds identifiers and labels only, never payloads.
 */
export type PlanEntry = Readonly<{
    planId: string;
    name: string;
    note: string | null;
    selectionId: string | null;
    sourceConnectionId: string | null;
    targetConnectionId: string | null;
    createdAt: string;
    updatedAt: string;
    sealed: boolean;
    plannedWrites: number | null;
    lastJobId: string | null;
}>;

export type SelectionEntry = Readonly<{
    selectionId: string;
    name: string;
    connectionId: string | null;
    snapshotId: string | null;
    rootTable: string | null;
    savedAt: string;
}>;

type RegistryState = {
    plans: Readonly<Record<string, PlanEntry>>;
    selections: Readonly<Record<string, SelectionEntry>>;
    upsertPlan: (entry: Partial<PlanEntry> & Readonly<{ planId: string }>) => void;
    forgetPlan: (planId: string) => void;
    upsertSelection: (entry: Partial<SelectionEntry> & Readonly<{ selectionId: string }>) => void;
    forgetSelection: (selectionId: string) => void;
};

let store: StoreApi<RegistryState> | null = null;

function registryStore(): StoreApi<RegistryState> {
    store ??= createStore<RegistryState>()(
        persist(
            (set) => ({
                plans: {},
                selections: {},
                upsertPlan: (entry) =>
                    set((state) => {
                        const now = new Date().toISOString();
                        const existing = state.plans[entry.planId];
                        const merged: PlanEntry = {
                            planId: entry.planId,
                            name: entry.name ?? existing?.name ?? '',
                            note: entry.note ?? existing?.note ?? null,
                            selectionId: entry.selectionId ?? existing?.selectionId ?? null,
                            sourceConnectionId: entry.sourceConnectionId ?? existing?.sourceConnectionId ?? null,
                            targetConnectionId: entry.targetConnectionId ?? existing?.targetConnectionId ?? null,
                            createdAt: existing?.createdAt ?? entry.createdAt ?? now,
                            updatedAt: now,
                            sealed: entry.sealed ?? existing?.sealed ?? false,
                            plannedWrites: entry.plannedWrites ?? existing?.plannedWrites ?? null,
                            lastJobId: entry.lastJobId ?? existing?.lastJobId ?? null,
                        };
                        return { plans: { ...state.plans, [entry.planId]: merged } };
                    }),
                forgetPlan: (planId) =>
                    set((state) => {
                        const plans = { ...state.plans };
                        delete plans[planId];
                        return { plans };
                    }),
                forgetSelection: (selectionId) =>
                    set((state) => {
                        const selections = { ...state.selections };
                        delete selections[selectionId];
                        return { selections };
                    }),
                upsertSelection: (entry) =>
                    set((state) => {
                        const existing = state.selections[entry.selectionId];
                        const merged: SelectionEntry = {
                            selectionId: entry.selectionId,
                            name: entry.name ?? existing?.name ?? '',
                            connectionId: entry.connectionId ?? existing?.connectionId ?? null,
                            snapshotId: entry.snapshotId ?? existing?.snapshotId ?? null,
                            rootTable: entry.rootTable ?? existing?.rootTable ?? null,
                            savedAt: existing?.savedAt ?? entry.savedAt ?? new Date().toISOString(),
                        };
                        return { selections: { ...state.selections, [entry.selectionId]: merged } };
                    }),
            }),
            {
                name: 'datapitcher.registry',
                storage: createJSONStorage(() => resolveStorage()),
                partialize: ({ plans, selections }) => ({ plans, selections }),
            },
        ),
    );
    return store;
}

export const registryActions = {
    upsertPlan: (entry: Partial<PlanEntry> & Readonly<{ planId: string }>) =>
        registryStore().getState().upsertPlan(entry),
    forgetPlan: (planId: string) => registryStore().getState().forgetPlan(planId),
    upsertSelection: (entry: Partial<SelectionEntry> & Readonly<{ selectionId: string }>) =>
        registryStore().getState().upsertSelection(entry),
    forgetSelection: (selectionId: string) => registryStore().getState().forgetSelection(selectionId),
    getPlan: (planId: string) => registryStore().getState().plans[planId] ?? null,
};

export const usePlanRegistry = () => useStore(registryStore(), (state) => state.plans);
export const useSelectionRegistry = () => useStore(registryStore(), (state) => state.selections);
export const usePlanEntry = (planId: string | null) =>
    useStore(registryStore(), (state) => (planId ? (state.plans[planId] ?? null) : null));
export const useSelectionEntry = (selectionId: string | null) =>
    useStore(registryStore(), (state) => (selectionId ? (state.selections[selectionId] ?? null) : null));
