import { expect, it } from 'vitest';
import { matchRoute } from './routeMatch';

it('matches the home route for an unrecognized path', () => {
  expect(matchRoute('/')).toEqual({ name: 'home' });
  expect(matchRoute('/nowhere')).toEqual({ name: 'home' });
});
it('matches connections and selection workbench with no parameters', () => {
  expect(matchRoute('/connections')).toEqual({ name: 'connections' });
  expect(matchRoute('/connections/')).toEqual({ name: 'connections' });
  expect(matchRoute('/selection-workbench')).toEqual({ name: 'selection-workbench' });
  expect(matchRoute('/selection-workbench/')).toEqual({ name: 'selection-workbench' });
});
it('matches the schema graph route with and without a plan id', () => {
  expect(matchRoute('/dependency-graph')).toEqual({ name: 'schema-graph', planId: null });
  expect(matchRoute('/dependency-graph/')).toEqual({ name: 'schema-graph', planId: null });
  expect(matchRoute('/dependency-graph/plan-1')).toEqual({ name: 'schema-graph', planId: 'plan-1' });
});
it('matches the plan review route with and without a plan id', () => {
  expect(matchRoute('/plan-review')).toEqual({ name: 'plan-review', planId: null });
  expect(matchRoute('/plan-review/plan-1')).toEqual({ name: 'plan-review', planId: 'plan-1' });
});
it('matches the transfer monitor route with and without a job id', () => {
  expect(matchRoute('/transfer-monitor')).toEqual({ name: 'transfer-monitor', jobId: null });
  expect(matchRoute('/transfer-monitor/job-1')).toEqual({ name: 'transfer-monitor', jobId: 'job-1' });
});
