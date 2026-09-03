import { createDevelopmentAuthenticationAdapter } from '../auth/authAdapter';
import { PermissionsProvider } from '../auth/permissions';
import { matchRoute } from './routeMatch';
import { routes } from './routes';
import { Link, useLocationPath } from './router';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'development-operator', tenantId: 'development' }, 'development-token');

export function App() {
  return <PermissionsProvider authentication={authentication}><AppShell /></PermissionsProvider>;
}

function AppShell() {
  const route = matchRoute(useLocationPath(), routes);
  return (
    <main>
      <header>
        <h1>DataPitcher</h1>
        <nav aria-label="Application">
          {routes.map((entry) => <Link key={entry.path} to={entry.path.replace(/\/:\w+\?$/, '')}>{entry.label}</Link>)}
        </nav>
      </header>
      {route ? route.route.render(route.params, { authentication }) : <section aria-label="Workspace"><p>Transfer planning workspace.</p></section>}
    </main>
  );
}
