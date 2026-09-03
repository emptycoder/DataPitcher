import { describe, expect, it } from 'vitest';
import { isActive, isTerminal, legalCommands, normalizeJobState } from './jobs';

describe('job state helpers', () => {
  it('normalizes PascalCase and lowercase server states', () => {
    expect(normalizeJobState('Running')).toBe('running');
    expect(normalizeJobState('VerificationFailed')).toBe('verificationfailed');
    expect(normalizeJobState('nonsense')).toBe('unknown');
  });

  it('classifies terminal and active states', () => {
    expect(isTerminal('Succeeded')).toBe(true);
    expect(isTerminal('running')).toBe(false);
    expect(isActive('Queued')).toBe(true);
    expect(isActive('Draft')).toBe(false);
    expect(isActive('cancelled')).toBe(false);
  });

  it('offers only legal commands per state', () => {
    expect(legalCommands('Running')).toEqual(['Pause', 'Cancel']);
    expect(legalCommands('Paused')).toEqual(['Resume', 'Cancel']);
    expect(legalCommands('Succeeded')).toEqual([]);
    expect(legalCommands('weird')).toEqual([]);
  });
});
