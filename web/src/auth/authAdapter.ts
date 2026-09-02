export type AuthenticatedPrincipal = Readonly<{ subjectId: string; tenantId: string }>;

export interface AuthenticationAdapter {
  getPrincipal(): Promise<AuthenticatedPrincipal | null>;
  getAccessToken(): Promise<string | null>;
  signOut(): Promise<void>;
}

export function createDevelopmentAuthenticationAdapter(
  principal: AuthenticatedPrincipal,
  token: string,
): AuthenticationAdapter {
  let activePrincipal: AuthenticatedPrincipal | null = principal;
  let activeToken: string | null = token;
  return {
    getPrincipal: async () => activePrincipal,
    getAccessToken: async () => activeToken,
    signOut: async () => { activePrincipal = null; activeToken = null; },
  };
}
