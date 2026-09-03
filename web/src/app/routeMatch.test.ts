import { expect, it } from 'vitest';
import { matchRoute } from './routeMatch';

const routes = [{ path: '/connections' }, { path: '/plans/:planId' }, { path: '/transfers/:jobId?' }] as const;

it('matches static routes and rejects unregistered paths', () => {
  expect(matchRoute('/connections/', routes)).toEqual({ route: routes[0], params: {} });
  expect(matchRoute('/nowhere', routes)).toBeNull();
});

it('matches required and optional path parameters', () => {
  expect(matchRoute('/plans/plan-1', routes)).toEqual({ route: routes[1], params: { planId: 'plan-1' } });
  expect(matchRoute('/transfers', routes)).toEqual({ route: routes[2], params: {} });
  expect(matchRoute('/transfers/job-1', routes)).toEqual({ route: routes[2], params: { jobId: 'job-1' } });
});

it('rejects paths with missing required or excess segments', () => {
  expect(matchRoute('/plans', routes)).toBeNull();
  expect(matchRoute('/connections/extra', routes)).toBeNull();
});
