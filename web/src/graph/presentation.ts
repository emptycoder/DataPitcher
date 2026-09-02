import type { GraphTableState } from './model';

export type GraphStatePresentation = Readonly<{ icon: string; label: string; borderClass: string }>;

export const graphStatePresentation: Readonly<Record<GraphTableState, GraphStatePresentation>> = {
  unselected: { icon: '○', label: 'Unselected', borderClass: 'border-slate-400' },
  'root-selected': { icon: '●', label: 'Root selected', borderClass: 'border-blue-700' },
  'required-dependency': { icon: '↗', label: 'Required dependency', borderClass: 'border-green-700' },
  'explicit-dependent': { icon: '↘', label: 'Explicit dependent', borderClass: 'border-violet-700' },
  'target-satisfied': { icon: '✓', label: 'Target satisfied', borderClass: 'border-teal-700' },
  blocked: { icon: '!', label: 'Blocked', borderClass: 'border-amber-700' },
  conflict: { icon: '⚠', label: 'Conflict', borderClass: 'border-red-700' },
  'cycle-member': { icon: '⟲', label: 'Cycle member', borderClass: 'border-orange-700' },
};

export function presentGraphState(state: GraphTableState): GraphStatePresentation {
  return graphStatePresentation[state];
}
