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
