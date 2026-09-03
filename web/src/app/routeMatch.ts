export type Route =
  | { name: 'home' }
  | { name: 'connections' }
  | { name: 'schema-graph'; planId: string | null }
  | { name: 'selection-workbench' }
  | { name: 'plan-review'; planId: string | null }
  | { name: 'transfer-monitor'; jobId: string | null };

function optionalSegment(pathname: string, prefix: string): string | null | undefined {
  const match = pathname.match(new RegExp(`^/${prefix}(?:/([^/]+))?/?$`));
  return match ? (match[1] ?? null) : undefined;
}

export function matchRoute(pathname: string): Route {
  const planId = optionalSegment(pathname, 'dependency-graph');
  if (planId !== undefined) return { name: 'schema-graph', planId };
  const reviewPlanId = optionalSegment(pathname, 'plan-review');
  if (reviewPlanId !== undefined) return { name: 'plan-review', planId: reviewPlanId };
  const jobId = optionalSegment(pathname, 'transfer-monitor');
  if (jobId !== undefined) return { name: 'transfer-monitor', jobId };
  if (/^\/selection-workbench\/?$/.test(pathname)) return { name: 'selection-workbench' };
  if (/^\/connections\/?$/.test(pathname)) return { name: 'connections' };
  return { name: 'home' };
}
