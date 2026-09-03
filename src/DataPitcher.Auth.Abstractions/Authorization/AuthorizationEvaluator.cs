using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;

namespace DataPitcher.Auth.Abstractions.Authorization;

public static class AuthorizationEvaluator
{
    public static AuthorizationDecision Evaluate(AuthorizationInput input, Permission requiredPermission)
    {
        if (input.PrincipalState is PrincipalAuthorizationState.TerminalDeny or PrincipalAuthorizationState.Disabled)
        {
            return new(AuthorizationOutcome.Denied, PermissionSet.Empty);
        }

        var effectivePermissions = EffectivePermissions(input.PositiveGrants);
        if (effectivePermissions.Contains(requiredPermission))
        {
            return new(AuthorizationOutcome.Granted, effectivePermissions);
        }

        if (
            input.GroupResolution.State == GroupResolutionState.Indeterminate
            && input.RolesThatMayBeGrantedByIndeterminateGroups.Any(role =>
                Enum.IsDefined(role) && RoleBundles.For(role).Contains(requiredPermission)
            )
        )
        {
            return new(AuthorizationOutcome.Indeterminate, effectivePermissions);
        }

        return new(AuthorizationOutcome.Denied, effectivePermissions);
    }

    public static PermissionSet EffectivePermissions(IEnumerable<RoleGrant> positiveGrants) =>
        positiveGrants
            .Where(grant => Enum.IsDefined(grant.Role))
            .Aggregate(PermissionSet.Empty, (permissions, grant) => permissions.Union(RoleBundles.For(grant.Role)));
}
