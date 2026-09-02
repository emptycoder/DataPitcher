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
