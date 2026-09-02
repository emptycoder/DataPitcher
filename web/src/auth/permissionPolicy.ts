export type ControlState = { visible: boolean; disabled: boolean; reason?: string };

export function controlState(
  requiredPermission: string,
  grantedPermissions: ReadonlySet<string>,
  prerequisiteMet: boolean,
  reason: string,
): ControlState {
  if (!grantedPermissions.has(requiredPermission)) return { visible: false, disabled: false };
  return prerequisiteMet ? { visible: true, disabled: false } : { visible: true, disabled: true, reason };
}
