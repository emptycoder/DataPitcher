import type { AuthenticationAdapter } from '../auth/authAdapter';
import type { RequestFunction } from './effectivePermissionsApi';
import { fetchPlanDependencyGraph } from './planDependencyGraphApi';

export function planDependencyGraphQueryOptions(planId: string, request: RequestFunction, authentication: AuthenticationAdapter) {
  return {
    queryKey: ['planDependencyGraph', planId] as const,
    staleTime: 30_000,
    retry: false,
    queryFn: ({ signal }: { signal: AbortSignal }) => fetchPlanDependencyGraph(planId, request, authentication, signal),
  };
}
