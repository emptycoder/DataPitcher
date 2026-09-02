import { create } from 'zustand';
import { createJSONStorage, persist, type StateStorage } from 'zustand/middleware';

type WorkbenchPreferencesState = {
  favouriteTableIds: readonly string[];
  recentTableIds: readonly string[];
  toggleFavourite: (tableId: string) => void;
  recordRecent: (tableId: string) => void;
};

export function createWorkbenchPreferences(storage: StateStorage) {
  const useWorkbenchPreferencesState = create<WorkbenchPreferencesState>()(persist(
    (set) => ({
      favouriteTableIds: [],
      recentTableIds: [],
      toggleFavourite: (tableId) => set((state) => ({
        favouriteTableIds: state.favouriteTableIds.includes(tableId)
          ? state.favouriteTableIds.filter((id) => id !== tableId)
          : [...state.favouriteTableIds, tableId],
      })),
      recordRecent: (tableId) => set((state) => ({
        recentTableIds: [tableId, ...state.recentTableIds.filter((id) => id !== tableId)].slice(0, 10),
      })),
    }),
    {
      name: 'datapitcher.selection-workbench',
      storage: createJSONStorage(() => storage),
      partialize: ({ favouriteTableIds, recentTableIds }) => ({ favouriteTableIds, recentTableIds }),
    },
  ));

  return {
    actions: {
      toggleFavourite: (tableId: string) => useWorkbenchPreferencesState.getState().toggleFavourite(tableId),
      recordRecent: (tableId: string) => useWorkbenchPreferencesState.getState().recordRecent(tableId),
    },
    useIsFavourite: (tableId: string) => useWorkbenchPreferencesState((state) => state.favouriteTableIds.includes(tableId)),
    useIsRecent: (tableId: string) => useWorkbenchPreferencesState((state) => state.recentTableIds.includes(tableId)),
  };
}
