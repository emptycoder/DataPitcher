import { expect, it } from 'vitest';
import { createSanitizedPlanExport, planTableStateLabel, startAvailability } from './planReviewModel';
import { reviewWire } from '../../test/planFixtures';

it.each([['Root', 'Root'], ['RequiredDependency', 'Required dependency'], ['ExplicitDependent', 'Explicit dependent'], ['TargetSatisfied', 'Target satisfied'], ['Excluded', 'Excluded'], ['Blocked', 'Blocked'], ['Conflict', 'Conflict'], ['CycleMember', 'Cycle member']])('labels every plan table state', (state, label) => expect(planTableStateLabel(state as never)).toBe(label));
it('disables stale and failed-precondition starts with server-supplied reasons', () => {
  const review = { ...reviewWire, seal: { status: 'invalidated', invalidationReasons: [{ code: 'target-schema', message: 'Target schema changed.' }] }, startPreconditions: [{ code: 'schemaValid', satisfied: false, message: 'Target schema validation failed.' }] };
  expect(startAvailability(review as never)).toEqual({ enabled: false, reasons: ['Target schema changed.', 'Target schema validation failed.'] });
});
it('exports only review-safe approval facts', () => {
  const exported = createSanitizedPlanExport(reviewWire as never);
  expect(exported).toContain('sales.Orders');
  expect(exported).not.toContain('Id=42');
  expect(exported).not.toContain('memory-token');
});
