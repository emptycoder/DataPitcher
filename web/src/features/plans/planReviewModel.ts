import { z } from 'zod';
import { PlanReviewResponse } from '../../api/generated/permissions.zod';

export type PlanReview = z.infer<typeof PlanReviewResponse>;
type PlanTableState = PlanReview['tables'][number]['state'];

export function planTableStateLabel(state: PlanTableState) {
  return ({ Root: 'Root', RequiredDependency: 'Required dependency', ExplicitDependent: 'Explicit dependent', TargetSatisfied: 'Target satisfied', Excluded: 'Excluded', Blocked: 'Blocked', Conflict: 'Conflict', CycleMember: 'Cycle member' } satisfies Record<PlanTableState, string>)[state];
}

export function startAvailability(review: PlanReview) {
  const reasons = [
    ...(review.seal.status === 'invalidated' ? review.seal.invalidationReasons.map((reason) => reason.message) : []),
    ...review.startPreconditions.filter((check) => !check.satisfied).map((check) => check.message),
  ];
  return { enabled: review.seal.status === 'sealed' && reasons.length === 0, reasons };
}

export function createSanitizedPlanExport(review: PlanReview) {
  return JSON.stringify({ planId: review.planId, version: review.version, canonicalHash: review.canonicalHash, seal: review.seal, totals: review.totals, tables: review.tables.map(({ source, target, state, transferOrder, included, plannedWrites, inserts, updates, estimatedBytes, columns }) => ({ source, target, state, transferOrder, included, plannedWrites, inserts, updates, estimatedBytes, columns })), conflicts: review.conflicts, cycles: review.cycles, warnings: review.warnings, blockers: review.blockers }, null, 2);
}
