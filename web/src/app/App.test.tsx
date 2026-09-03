import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';

vi.mock('../features/connections/ConnectionsScreen', () => ({ ConnectionsScreen: () => <section aria-label="Connections screen" /> }));
vi.mock('../features/graph/GraphScreen', () => ({ GraphScreen: () => <section aria-label="Schema graph screen" /> }));
vi.mock('../features/selections/SelectionWorkbenchScreen', () => ({ SelectionWorkbenchScreen: () => <section aria-label="Selection workbench screen" /> }));
vi.mock('../features/plans/PlanReview', () => ({ PlanReview: () => <section aria-label="Detailed plan review screen" /> }));
vi.mock('../features/plans/PlanReviewScreen', () => ({ PlanReviewScreen: ({ onJobStarted }: Readonly<{ onJobStarted: (jobId: string) => void }>) => <section aria-label="Plan review screen"><button type="button" onClick={() => onJobStarted('job-1')}>Start mocked transfer</button></section> }));
vi.mock('../features/schema/SchemaBrowserScreen', () => ({ SchemaBrowserScreen: () => <section aria-label="Schema browser screen" /> }));
vi.mock('../features/transfers/TransferMonitorScreen', () => ({ TransferMonitorScreen: ({ scheduler }: Readonly<{ scheduler: Readonly<{ setTimeout: (work: () => void, delay: number) => unknown; clearTimeout: (handle: unknown) => void }> }>) => <section aria-label="Transfer monitor screen"><button type="button" onClick={() => { const timer = scheduler.setTimeout(() => undefined, 0); scheduler.clearTimeout(timer); }}>Schedule mocked reconnect</button></section> }));

import { App } from './App';
import { AppProviders } from './AppProviders';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 404 })));
});

afterEach(() => {
  cleanup();
  window.history.replaceState(null, '', '/');
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it('renders the application landmark and name', () => {
  render(<App />);
  expect(screen.getByRole('main')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'DataPitcher' })).toBeVisible();
});

it.each([
  ['/connections', 'Connections screen'],
  ['/dependency-graph', 'Schema graph screen'],
  ['/dependency-graph/plan-1', 'Schema graph screen'],
  ['/selection-workbench', 'Selection workbench screen'],
  ['/plan-review', 'Plan review screen'],
  ['/plan-review/plan-1', 'Plan review screen'],
  ['/plans/plan-1/review', 'Detailed plan review screen'],
  ['/transfer-monitor', 'Transfer monitor screen'],
  ['/transfer-monitor/job-1', 'Transfer monitor screen'],
  ['/schema-browser', 'Schema browser screen'],
])('routes the shell to %s', (path, screenName) => {
  window.history.replaceState(null, '', path);
  render(<AppProviders><App /></AppProviders>);

  expect(screen.getByRole('main')).toBeVisible();
  expect(screen.getByRole('region', { name: screenName })).toBeVisible();
  expect(screen.getByRole('link', { name: 'Connections' })).toHaveAttribute('href', '/connections');
});

it('runs route callbacks through the history shell', () => {
  window.history.replaceState(null, '', '/plan-review/plan-1');
  render(<AppProviders><App /></AppProviders>);

  fireEvent.click(screen.getByRole('button', { name: 'Start mocked transfer' }));
  expect(window.location.pathname).toBe('/transfer-monitor/job-1');
  fireEvent.click(screen.getByRole('button', { name: 'Schedule mocked reconnect' }));
});
