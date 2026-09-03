import { decodeJwt, isExpired, rolesFromClaims } from './jwt';

export type AuthenticatedPrincipal = Readonly<{ subjectId: string; tenantId: string; roles: readonly string[]; expiresAt: number | null }>;

export interface AuthenticationAdapter {
  getPrincipal(): Promise<AuthenticatedPrincipal | null>;
  getAccessToken(): Promise<string | null>;
  signOut(): Promise<void>;
}

/** Keeps the access token inside a closure: it is never placed in a store, browser storage, or URL. */
export function createTokenAuthenticationAdapter(token: string, onSignOut?: () => void): AuthenticationAdapter {
  const claims = decodeJwt(token);
  const principal: AuthenticatedPrincipal = {
    subjectId: typeof claims?.sub === 'string' ? claims.sub : 'unknown',
    tenantId: typeof claims?.tid === 'string' ? claims.tid : 'development',
    roles: rolesFromClaims(claims),
    expiresAt: typeof claims?.exp === 'number' ? claims.exp * 1000 : null,
  };
  let activeToken: string | null = token;
  return {
    getPrincipal: async () => (activeToken ? principal : null),
    getAccessToken: async () => (activeToken && !isExpired(decodeJwt(activeToken)) ? activeToken : null),
    signOut: async () => {
      activeToken = null;
      onSignOut?.();
    },
  };
}

/** Compatibility helper for the in-memory development adapter used by the original scaffold. */
export function createDevelopmentAuthenticationAdapter(
  principal: Readonly<{ subjectId: string; tenantId: string }>,
  token: string,
): AuthenticationAdapter {
  let activePrincipal: AuthenticatedPrincipal | null = { ...principal, roles: [], expiresAt: null };
  let activeToken: string | null = token;
  return {
    getPrincipal: async () => activePrincipal,
    getAccessToken: async () => activeToken,
    signOut: async () => {
      activePrincipal = null;
      activeToken = null;
    },
  };
}
