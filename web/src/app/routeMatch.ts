export type PathRoute = Readonly<{ path: string }>;
export type RouteParams = Readonly<Record<string, string>>;

function matchPath(pattern: string, pathname: string): RouteParams | null {
  const patternSegments = pattern.split('/').filter(Boolean);
  const pathSegments = pathname.split('/').filter(Boolean);
  const params: Record<string, string> = {};
  let pathIndex = 0;

  for (const segment of patternSegments) {
    const pathSegment = pathSegments[pathIndex];
    if (segment.startsWith(':')) {
      const optional = segment.endsWith('?');
      if (pathSegment) {
        params[segment.slice(1, optional ? -1 : undefined)] = pathSegment;
        pathIndex += 1;
      } else if (!optional) {
        return null;
      }
    } else if (pathSegment === segment) {
      pathIndex += 1;
    } else {
      return null;
    }
  }

  return pathIndex === pathSegments.length ? params : null;
}

export function matchRoute<Route extends PathRoute>(pathname: string, routes: readonly Route[]): Readonly<{ route: Route; params: RouteParams }> | null {
  for (const route of routes) {
    const params = matchPath(route.path, pathname);
    if (params) return { route, params };
  }
  return null;
}
