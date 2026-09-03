import { afterEach, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { sessionActions, useSessionIdentity, useSourceConnectionId, useTargetConnectionId } from './sessionStore';

function SessionProbe() {
  const identity = useSessionIdentity();
  const sourceId = useSourceConnectionId();
  const targetId = useTargetConnectionId();
  return <output>{`${identity?.subjectId}|${sourceId}|${targetId}`}</output>;
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
