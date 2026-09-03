import { useMemo } from 'react';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { ConnectionsScreen } from '../features/connections/ConnectionsScreen';
import { PlanReviewScreen } from '../features/plans/PlanReviewScreen';
import { SelectionWorkbenchScreen } from '../features/selections/SelectionWorkbenchScreen';
import { TransferMonitorScreen } from '../features/transfers/TransferMonitorScreen';
import { DependencyGraphScreen } from '../graph/DependencyGraphScreen';
import { createElkLayoutAdapter } from '../graph/elkLayout';
import { createLayoutResultCache, type LayoutScheduler } from '../graph/layout';
import { matchRoute } from './routeMatch';
import { Link, navigate, useLocationPath } from './router';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'development-operator', tenantId: 'development' }, 'development-token');
const immediateScheduler: LayoutScheduler = (work) => work();
const reconnectScheduler = { setTimeout: (work: () => void, delay: number) => window.setTimeout(work, delay), clearTimeout: (handle: unknown) => window.clearTimeout(handle as number) };

function DependencyGraphRoute({ planId }: Readonly<{ planId: string | null }>) {
  const layoutAdapter = useMemo(createElkLayoutAdapter, []);
  const cache = useMemo(createLayoutResultCache, []);
  return <DependencyGraphScreen planId={planId} request={fetch} authentication={authentication} layoutAdapter={layoutAdapter} cache={cache} scheduler={immediateScheduler} />;
}

export function App() {
  const route = matchRoute(useLocationPath());
  return (
    <main>
      <h1>DataPitcher</h1>
      <nav aria-label="Application">
        <Link to="/connections">Connections</Link>
        <Link to="/dependency-graph">Schema graph</Link>
        <Link to="/selection-workbench">Selection workbench</Link>
        <Link to="/plan-review">Plan review</Link>
        <Link to="/transfer-monitor">Transfer monitor</Link>
      </nav>
      {route.name === 'connections' ? <ConnectionsScreen request={fetch} authentication={authentication} /> : null}
      {route.name === 'schema-graph' ? <DependencyGraphRoute planId={route.planId} /> : null}
      {route.name === 'selection-workbench' ? <SelectionWorkbenchScreen request={fetch} authentication={authentication} /> : null}
      {route.name === 'plan-review' ? <PlanReviewScreen planId={route.planId} request={fetch} authentication={authentication} onJobStarted={(jobId) => navigate(`/transfer-monitor/${jobId}`)} /> : null}
      {route.name === 'transfer-monitor' ? <TransferMonitorScreen jobId={route.jobId} request={fetch} authentication={authentication} clock={Date.now} scheduler={reconnectScheduler} /> : null}
      {route.name === 'home' ? <section aria-label="Workspace"><p>Transfer planning workspace.</p></section> : null}
    </main>
  );
}
