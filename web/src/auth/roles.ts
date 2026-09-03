export const allPermissions = [
  'Audit.Read',
  'Audit.Write',
  'AuthProviders.Manage',
  'Connections.Read',
  'Connections.Write',
  'Plans.Read',
  'Plans.Seal',
  'Plans.Write',
  'RoleMappings.Manage',
  'Schema.Read',
  'Schema.Write',
  'Selections.RawSql',
  'Selections.Read',
  'Selections.Write',
  'Transfers.ConstraintOverride',
  'Transfers.Read',
  'Transfers.Start',
  'Transfers.TriggerOverride',
  'Transfers.UsePotentiallyLossyMapping',
  'Transfers.Write',
] as const;

export type PermissionName = (typeof allPermissions)[number];
export type RoleName = 'Viewer' | 'Planner' | 'Operator' | 'Administrator';

const viewer: readonly PermissionName[] = ['Audit.Read', 'Connections.Read', 'Plans.Read', 'Schema.Read', 'Selections.Read', 'Transfers.Read'];
const planner: readonly PermissionName[] = [...viewer, 'Connections.Write', 'Plans.Seal', 'Plans.Write', 'Schema.Write', 'Selections.RawSql', 'Selections.Write'];
const operator: readonly PermissionName[] = [...viewer, 'Transfers.ConstraintOverride', 'Transfers.Start', 'Transfers.TriggerOverride', 'Transfers.UsePotentiallyLossyMapping'];

export const roleDescriptions: Readonly<Record<RoleName, string>> = {
  Viewer: 'Read-only access to connections, schemas, selections, plans and transfers.',
  Planner: 'Everything a viewer can do, plus managing connections, scanning schemas, authoring selections and sealing plans.',
  Operator: 'Everything a viewer can do, plus starting transfers and applying overrides.',
  Administrator: 'Every permission, including administrative ones.',
};

export const roleNames: readonly RoleName[] = ['Viewer', 'Planner', 'Operator', 'Administrator'];

/** Mirrors DataPitcher.Core.Authorization.RoleBundles so the client can derive permissions from a token's roles. */
export function permissionsForRoles(roles: readonly string[]): ReadonlySet<string> {
  const granted = new Set<string>();
  for (const role of roles) {
    const bundle =
      role === 'Administrator' ? allPermissions : role === 'Planner' ? planner : role === 'Operator' ? operator : role === 'Viewer' ? viewer : [];
    for (const permission of bundle) granted.add(permission);
  }
  return granted;
}
