import type { AuthenticatedPrincipal, AuthenticationAdapter } from '../auth/authAdapter';
import { fetchEffectivePermissions, type RequestFunction } from './effectivePermissionsApi';

export function effectivePermissionsQueryOptions(principal: AuthenticatedPrincipal, request: RequestFunction, authentication: AuthenticationAdapter) {
  return {
    queryKey: ['effectivePermissions', principal.subjectId, principal.tenantId] as const,
    staleTime: 30_000,
    retry: false,
    queryFn: ({ signal }: { signal: AbortSignal }) => fetchEffectivePermissions(request, authentication, signal),
  };
}
