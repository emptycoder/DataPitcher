import { useMemo, type ReactNode } from 'react';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { ConnectionsScreen } from '../features/connections/ConnectionsScreen';
import { PlanReview } from '../features/plans/PlanReview';
import { PlanReviewScreen } from '../features/plans/PlanReviewScreen';
import { SelectionWorkbenchScreen } from '../features/selections/SelectionWorkbenchScreen';
import { JobDetailScreen, JobsListScreen } from '../features/jobs/JobsScreens';
import { TransferMonitorScreen } from '../features/transfers/TransferMonitorScreen';
import { DependencyGraphScreen } from '../graph/DependencyGraphScreen';
import { createElkLayoutAdapter } from '../graph/elkLayout';
import { createLayoutResultCache, type LayoutScheduler } from '../graph/layout';
import { navigate } from './router';
import type { PathRoute, RouteParams } from './routeMatch';

export type RouteContext = Readonly<{ authentication: AuthenticationAdapter }>;
export type RouteRecord = PathRoute & Readonly<{ label: string; render: (params: RouteParams, context: RouteContext) => ReactNode }>;

const immediateScheduler: LayoutScheduler = (work) => work();
const reconnectScheduler = { setTimeout: (work: () => void, delay: number) => window.setTimeout(work, delay), clearTimeout: (handle: unknown) => window.clearTimeout(handle as number) };

function DependencyGraphRoute({ planId, authentication }: Readonly<{ planId: string | null; authentication: AuthenticationAdapter }>) {
  const layoutAdapter = useMemo(() => createElkLayoutAdapter(), []);
  const cache = useMemo(() => createLayoutResultCache(), []);
  return <DependencyGraphScreen planId={planId} request={fetch} authentication={authentication} layoutAdapter={layoutAdapter} cache={cache} scheduler={immediateScheduler} />;
}

function connectionsRoute(_: RouteParams, context: RouteContext) {
  return <ConnectionsScreen request={fetch} authentication={context.authentication} />;
}

function dependencyGraphRoute(params: RouteParams, context: RouteContext) {
  return <DependencyGraphRoute planId={params.planId ?? null} authentication={context.authentication} />;
}

function planReviewRoute(params: RouteParams, context: RouteContext) {
  return <PlanReviewScreen planId={params.planId ?? null} request={fetch} authentication={context.authentication} onJobStarted={(jobId) => navigate(`/transfer-monitor/${jobId}`)} />;
}

function selectionWorkbenchRoute(_: RouteParams, context: RouteContext) {
  return <SelectionWorkbenchScreen authentication={context.authentication} />;
}

function transferMonitorRoute(params: RouteParams, context: RouteContext) {
  return <TransferMonitorScreen jobId={params.jobId ?? null} request={fetch} authentication={context.authentication} clock={Date.now} scheduler={reconnectScheduler} />;
}

function jobsRoute(params: RouteParams, context: RouteContext) {
  return params.jobId ? <JobDetailScreen jobId={params.jobId} authentication={context.authentication} /> : <JobsListScreen />;
}

export const routes = [
  { path: '/connections', label: 'Connections', render: connectionsRoute },
  { path: '/dependency-graph/:planId?', label: 'Schema graph', render: dependencyGraphRoute },
  { path: '/selection-workbench', label: 'Selection workbench', render: selectionWorkbenchRoute },
  { path: '/plan-review/:planId?', label: 'Plan review', render: planReviewRoute },
  { path: '/plans/:planId/review', label: 'Plan review', render: (params, context) => <PlanReview planId={params.planId!} authentication={context.authentication} /> },
  { path: '/transfer-monitor/:jobId?', label: 'Transfer monitor', render: transferMonitorRoute },
  { path: '/jobs/:jobId?', label: 'Transfer jobs', render: jobsRoute },
] satisfies readonly RouteRecord[];
