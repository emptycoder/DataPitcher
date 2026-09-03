import type { ReactNode } from 'react';
import { ConnectionsScreen } from '../features/connections/ConnectionsScreen';
import { OverviewScreen } from '../features/overview/OverviewScreen';
import { PlanBuilderScreen } from '../features/plans/PlanBuilderScreen';
import { PlanDetailScreen } from '../features/plans/PlanDetailScreen';
import { PlansScreen } from '../features/plans/PlansScreen';
import { SchemaExplorerScreen } from '../features/schema/SchemaExplorerScreen';
import { SelectionsScreen } from '../features/selections/SelectionsScreen';
import { SelectionWorkbenchScreen } from '../features/selections/SelectionWorkbenchScreen';
import { TransferDetailScreen } from '../features/transfers/TransferDetailScreen';
import { TransfersScreen } from '../features/transfers/TransfersScreen';
import type { IconName } from '../ui/icons';
import type { PathRoute, RouteParams } from './routeMatch';

export type RouteRecord = PathRoute & Readonly<{ render: (params: RouteParams) => ReactNode; nav?: Readonly<{ label: string; icon: IconName }> }>;

export const routes = [
  { path: '/', nav: { label: 'Overview', icon: 'Home' }, render: () => <OverviewScreen /> },
  { path: '/connections', nav: { label: 'Connections', icon: 'Plug' }, render: () => <ConnectionsScreen /> },
  {
    path: '/schema/:connectionId?/:snapshotId?',
    nav: { label: 'Schema', icon: 'Schema' },
    render: (params) => <SchemaExplorerScreen connectionId={params.connectionId ?? null} snapshotId={params.snapshotId ?? null} />,
  },
  { path: '/selections', nav: { label: 'Selections', icon: 'Filter' }, render: () => <SelectionsScreen /> },
  { path: '/selections/new', render: () => <SelectionWorkbenchScreen /> },
  { path: '/selections/:selectionId/edit', render: (params) => <SelectionWorkbenchScreen selectionId={params.selectionId!} /> },
  { path: '/plans', nav: { label: 'Plans', icon: 'Clipboard' }, render: () => <PlansScreen /> },
  { path: '/plans/new', render: () => <PlanBuilderScreen planId={null} /> },
  { path: '/plans/:planId/edit', render: (params) => <PlanBuilderScreen planId={params.planId!} /> },
  { path: '/plans/:planId', render: (params) => <PlanDetailScreen planId={params.planId!} /> },
  { path: '/transfers', nav: { label: 'Transfers', icon: 'Rocket' }, render: () => <TransfersScreen /> },
  { path: '/transfers/:jobId', render: (params) => <TransferDetailScreen jobId={params.jobId!} /> },
] satisfies readonly RouteRecord[];

export const navRoutes = routes.filter((route) => route.nav !== undefined);

export function navPath(route: RouteRecord) {
  return route.path.replace(/(\/:\w+\?)+$/, '') || '/';
}

export function isNavActive(route: RouteRecord, pathname: string) {
  const base = navPath(route);
  return base === '/' ? pathname === '/' : pathname === base || pathname.startsWith(`${base}/`);
}
