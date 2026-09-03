import { useStore } from 'zustand';
import { createStore, type StoreApi } from 'zustand/vanilla';
import { createJSONStorage, persist } from 'zustand/middleware';
import { resolveStorage } from './persistence';

export type ThemePreference = 'system' | 'light' | 'dark';

type PreferencesState = {
  theme: ThemePreference;
  sidebarCollapsed: boolean;
  setTheme: (theme: ThemePreference) => void;
  toggleSidebar: () => void;
};

let store: StoreApi<PreferencesState> | null = null;

function preferencesStore(): StoreApi<PreferencesState> {
  store ??= createStore<PreferencesState>()(
    persist(
      (set) => ({
        theme: 'system',
        sidebarCollapsed: false,
        setTheme: (theme) => set({ theme }),
        toggleSidebar: () => set((state) => ({ sidebarCollapsed: !state.sidebarCollapsed })),
      }),
      {
        name: 'datapitcher.preferences',
        storage: createJSONStorage(() => resolveStorage()),
        partialize: ({ theme, sidebarCollapsed }) => ({ theme, sidebarCollapsed }),
      },
    ),
  );
  return store;
}

export const preferencesActions = {
  setTheme: (theme: ThemePreference) => preferencesStore().getState().setTheme(theme),
  toggleSidebar: () => preferencesStore().getState().toggleSidebar(),
};
export const useThemePreference = () => useStore(preferencesStore(), (state) => state.theme);
export const useSidebarCollapsed = () => useStore(preferencesStore(), (state) => state.sidebarCollapsed);
