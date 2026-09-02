import type { ReactNode } from 'react';
import { controlState } from './permissionPolicy';

type ProtectedActionProps = {
  requiredPermission: string;
  grantedPermissions: ReadonlySet<string>;
  prerequisiteMet: boolean;
  reason: string;
  children: ReactNode;
};

export function ProtectedAction(props: ProtectedActionProps) {
  const state = controlState(props.requiredPermission, props.grantedPermissions, props.prerequisiteMet, props.reason);
  if (!state.visible) return null;
  return <><button disabled={state.disabled}>{props.children}</button>{state.reason && <p>{state.reason}</p>}</>;
}
