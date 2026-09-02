import { create } from 'zustand';
import type { VisualSelection } from './selectionAst';

type DraftMode = 'visual' | 'raw';
type WorkbenchTab = 'visual' | 'sql' | 'preview' | 'explain';
type SelectionDraft = {
  selectionName: string;
  visual: VisualSelection | null;
  lastVisualAst: VisualSelection | null;
  sqlSnapshot: string | null;
  rawSql: string | null;
  mode: DraftMode;
  tab: WorkbenchTab;
  dirty: boolean;
  pendingVisualConfirmation: boolean;
};
type DraftState = SelectionDraft & {
  begin: (visual: VisualSelection) => void;
  clear: () => void;
  setSelectionName: (selectionName: string) => void;
  editVisual: (visual: VisualSelection) => void;
  setSqlSnapshot: (sqlSnapshot: string) => void;
  editRawSql: (rawSql: string) => void;
  requestVisualMode: () => void;
  cancelVisualMode: () => void;
  confirmDiscardRawSql: () => void;
  setTab: (tab: WorkbenchTab) => void;
};

const emptyDraft: SelectionDraft = {
  selectionName: '',
  visual: null,
  lastVisualAst: null,
  sqlSnapshot: null,
  rawSql: null,
  mode: 'visual',
  tab: 'visual',
  dirty: false,
  pendingVisualConfirmation: false,
};

const useDraftState = create<DraftState>()((set, get) => ({
  ...emptyDraft,
  begin: (visual) => set({ ...emptyDraft, visual, lastVisualAst: visual }),
  clear: () => set(emptyDraft),
  setSelectionName: (selectionName) => set({ selectionName, dirty: true }),
  editVisual: (visual) => {
    if (get().mode !== 'visual') return;
    set({ visual, lastVisualAst: visual, dirty: true });
  },
  setSqlSnapshot: (sqlSnapshot) => set({ sqlSnapshot }),
  editRawSql: (rawSql) => {
    if (!get().sqlSnapshot) return;
    set({ rawSql, mode: 'raw', dirty: true });
  },
  requestVisualMode: () => {
    if (get().mode === 'raw') set({ pendingVisualConfirmation: true });
  },
  cancelVisualMode: () => set({ pendingVisualConfirmation: false }),
  confirmDiscardRawSql: () => set((state) => ({
    visual: state.lastVisualAst,
    rawSql: null,
    mode: 'visual',
    pendingVisualConfirmation: false,
  })),
  setTab: (tab) => set({ tab }),
}));

export const draftActions = {
  begin: (visual: VisualSelection) => useDraftState.getState().begin(visual),
  clear: () => useDraftState.getState().clear(),
  setSelectionName: (selectionName: string) => useDraftState.getState().setSelectionName(selectionName),
  editVisual: (visual: VisualSelection) => useDraftState.getState().editVisual(visual),
  setSqlSnapshot: (sqlSnapshot: string) => useDraftState.getState().setSqlSnapshot(sqlSnapshot),
  editRawSql: (rawSql: string) => useDraftState.getState().editRawSql(rawSql),
  requestVisualMode: () => useDraftState.getState().requestVisualMode(),
  cancelVisualMode: () => useDraftState.getState().cancelVisualMode(),
  confirmDiscardRawSql: () => useDraftState.getState().confirmDiscardRawSql(),
  setTab: (tab: WorkbenchTab) => useDraftState.getState().setTab(tab),
  snapshot: (): SelectionDraft => useDraftState.getState(),
};
export const useDraftMode = () => useDraftState((state) => state.mode);
export const useDraftTab = () => useDraftState((state) => state.tab);
export const useDraftDirty = () => useDraftState((state) => state.dirty);
export const useDraftSelectionName = () => useDraftState((state) => state.selectionName);
export const usePendingVisualConfirmation = () => useDraftState((state) => state.pendingVisualConfirmation);
