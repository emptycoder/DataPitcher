import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { requestJson } from '../api/http';
import { EffectivePermissionsResponse } from '../api/generated/permissions.zod';
import type { AuthenticationAdapter } from './authAdapter';
import { permissionsForRoles } from './roles';

export type Permissions = Readonly<{
  /** True once the set came from the server (or was derived from the token's roles). */
  isVerified: boolean;
  source: 'server' | 'roles' | 'unknown';
  granted: ReadonlySet<string>;
  hasPermission: (permission: string) => boolean;
}>;

const unverified: Permissions = { isVerified: false, source: 'unknown', granted: new Set(), hasPermission: () => true };
const PermissionsContext = createContext<Permissions>(unverified);

export function PermissionsProvider({
  authentication,
  roles,
  children,
}: Readonly<{ authentication: AuthenticationAdapter; roles: readonly string[]; children: ReactNode }>) {
  const [result, setResult] = useState<Readonly<{ authentication: AuthenticationAdapter; server: ReadonlySet<string> | null }> | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void requestJson<unknown>('/api/auth/effective-permissions', authentication, { signal: controller.signal })
      .then((response) => {
        const parsed = EffectivePermissionsResponse.safeParse(response);
        return parsed.success ? new Set(parsed.data.permissions) : null;
      })
      .catch(() => null)
      .then((server) => {
        if (!controller.signal.aborted) setResult({ authentication, server });
      });
    return () => controller.abort();
  }, [authentication]);

  const value = useMemo<Permissions>(() => {
    const current = result?.authentication === authentication ? result : null;
    const server = current?.server ?? null;
    if (server) return { isVerified: true, source: 'server', granted: server, hasPermission: (permission) => server.has(permission) };
    if (!current) return unverified;
    const derived = permissionsForRoles(roles);
    return { isVerified: true, source: 'roles', granted: derived, hasPermission: (permission) => derived.has(permission) };
  }, [result, authentication, roles]);

  return <PermissionsContext value={value}>{children}</PermissionsContext>;
}

export function usePermissions(): Permissions {
  return useContext(PermissionsContext);
}
