import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { requestJson } from '../api/http';
import { EffectivePermissionsResponse } from '../api/generated/permissions.zod';
import type { AuthenticationAdapter } from './authAdapter';

export type Permissions = Readonly<{
  isVerified: boolean;
  hasPermission: (permission: string) => boolean;
}>;

const unverifiedPermissions: Permissions = { isVerified: false, hasPermission: () => true };
const PermissionsContext = createContext<Permissions>(unverifiedPermissions);

export type PermissionsProviderProps = Readonly<{ authentication: AuthenticationAdapter; children: ReactNode }>;

export function PermissionsProvider({ authentication, children }: PermissionsProviderProps) {
  const [permissions, setPermissions] = useState<Permissions>(unverifiedPermissions);

  useEffect(() => {
    const controller = new AbortController();
    void requestJson<unknown>('/api/auth/effective-permissions', authentication, { signal: controller.signal })
      .then((response) => {
        const parsed = EffectivePermissionsResponse.safeParse(response);
        if (parsed.success) {
          const granted = new Set(parsed.data.permissions);
          setPermissions({ isVerified: true, hasPermission: (permission) => granted.has(permission) });
        }
      })
      .catch(() => undefined);
    return () => controller.abort();
  }, [authentication]);

  return <PermissionsContext value={permissions}>{children}</PermissionsContext>;
}

export function usePermissions(): Permissions {
  return useContext(PermissionsContext);
}
