import { afterEach, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { createPreferencesStore } from './preferencesStore';
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
  cleanup();
  sessionActions.setIdentity(null);
  sessionActions.setConnectionIds(null, null);
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
  preferences.actions.setReducedMotion(false);
  render(<PreferenceProbe preferences={preferences} />);
  expect(screen.getByRole('status')).toHaveTextContent('dark|false');
  expect(JSON.parse(values.get('datapitcher.preferences')!).state)
    .toEqual({ colorScheme: 'dark', reducedMotion: false });
});
