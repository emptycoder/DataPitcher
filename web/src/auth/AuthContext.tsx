import { createContext, useContext } from 'react';
import type { AuthenticatedPrincipal, AuthenticationAdapter } from './authAdapter';

export type AuthSession = Readonly<{ authentication: AuthenticationAdapter; principal: AuthenticatedPrincipal; signOut: () => void }>;

const AuthContext = createContext<AuthSession | null>(null);
export const AuthProvider = AuthContext;

export function useAuth(): AuthSession {
  const session = useContext(AuthContext);
  if (!session) throw new Error('useAuth must be used inside an authenticated session.');
  return session;
}
