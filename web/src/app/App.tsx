import { useCallback, useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { AuthProvider, type AuthSession } from '../auth/AuthContext';
import { createTokenAuthenticationAdapter } from '../auth/authAdapter';
import { loadRememberedSignIn, rememberSignIn } from '../auth/devSession';
import { mintDevelopmentToken } from '../auth/devToken';
import { PermissionsProvider } from '../auth/permissions';
import { SignInScreen, type SignInResult } from '../auth/SignInScreen';
import { sessionActions } from '../stores/sessionStore';
import { Spinner } from '../ui';
import { Shell } from './Shell';
import { useApplyTheme } from './theme';

type Phase = 'restoring' | 'signed-out' | 'signed-in';

export function App() {
  useApplyTheme();
  const queryClient = useQueryClient();
  const [phase, setPhase] = useState<Phase>(() => (loadRememberedSignIn()?.signingKey ? 'restoring' : 'signed-out'));
  const [session, setSession] = useState<AuthSession | null>(null);

  const signOut = useCallback(() => {
    rememberSignIn(null);
    sessionActions.setIdentity(null);
    queryClient.clear();
    setSession(null);
    setPhase('signed-out');
  }, [queryClient]);

  const establish = useCallback(
    async (token: string) => {
      const authentication = createTokenAuthenticationAdapter(token, signOut);
      const principal = await authentication.getPrincipal();
      if (!principal) throw new Error('Token rejected.');
      sessionActions.setIdentity({ subjectId: principal.subjectId, tenantId: principal.tenantId });
      setSession({ authentication, principal, signOut: () => void authentication.signOut() });
      setPhase('signed-in');
    },
    [signOut],
  );

  useEffect(() => {
    let cancelled = false;
    const remembered = loadRememberedSignIn();
    if (!remembered || !remembered.signingKey) return;
    mintDevelopmentToken(remembered)
      .then((token) => (cancelled ? undefined : establish(token)))
      .catch(() => {
        if (!cancelled) setPhase('signed-out');
      });
    return () => {
      cancelled = true;
    };
  }, [establish]);

  const onSignedIn = useCallback(
    (result: SignInResult) => {
      rememberSignIn(result.remember);
      void establish(result.token);
    },
    [establish],
  );

  const roles = useMemo(() => session?.principal.roles ?? [], [session]);

  if (phase === 'restoring') {
    return (
      <div className="flex min-h-screen items-center justify-center text-fg-muted">
        <Spinner size={22} />
      </div>
    );
  }
  if (phase === 'signed-out' || !session) return <SignInScreen initial={loadRememberedSignIn()} onSignedIn={onSignedIn} />;

  return (
    <AuthProvider value={session}>
      <PermissionsProvider authentication={session.authentication} roles={roles}>
        <Shell />
      </PermissionsProvider>
    </AuthProvider>
  );
}
