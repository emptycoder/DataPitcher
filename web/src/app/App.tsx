import { useMemo } from 'react';
import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { DependencyGraphScreen } from '../graph/DependencyGraphScreen';
import { createElkLayoutAdapter } from '../graph/elkLayout';
import { createLayoutResultCache, type LayoutScheduler } from '../graph/layout';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'development-operator', tenantId: 'development' }, 'development-token');
const immediateScheduler: LayoutScheduler = (work) => work();

function DependencyGraphRoute({ planId }: Readonly<{ planId: string | null }>) {
  const layoutAdapter = useMemo(createElkLayoutAdapter, []);
  const cache = useMemo(createLayoutResultCache, []);
  return <DependencyGraphScreen planId={planId} request={fetch} authentication={authentication} layoutAdapter={layoutAdapter} cache={cache} scheduler={immediateScheduler} />;
}

export function App() {
  const graphRoute = window.location.pathname.match(/^\/dependency-graph(?:\/([^/]+))?\/?$/);
  return (
    <main>
      <h1>DataPitcher</h1>
      <nav><a href="/dependency-graph">Dependency graph</a></nav>
      {graphRoute ? <DependencyGraphRoute planId={graphRoute[1] ?? null} /> : <p>Transfer planning workspace.</p>}
    </main>
  );
}
