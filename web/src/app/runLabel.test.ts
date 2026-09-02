import { expect, it } from 'vitest';
import { frontendLaneLabel } from './runLabel';

it('names the isolated frontend test lane', () => {
  expect(frontendLaneLabel).toBe('frontend');
});
