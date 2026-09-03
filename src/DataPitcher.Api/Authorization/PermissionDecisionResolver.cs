using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Api.Authorization;

public interface IPermissionDecisionResolver
{
    AuthorizationDecision Resolve(ClaimsPrincipal principal, Permission permission);
}

/// <summary>
/// Derives authorization decisions from the validated principal's "roles"/"groups" claims through
/// <see cref="AuthorizationEvaluator"/>. A "permission" claim is never consulted: an external issuer that can
/// assert an arbitrary claim of that shape must not be able to self-grant authority. There is no persisted
/// principal-disable list yet, so every authenticated principal is treated as
/// <see cref="PrincipalAuthorizationState.Active"/>; only "roles" claim values that match a known
/// <see cref="Role"/> name exactly produce a grant.
/// </summary>
public sealed class ClaimsPermissionDecisionResolver : IPermissionDecisionResolver
{
    private const string RolesClaimType = "roles";
    private const string GroupsClaimType = "groups";
    private const string GroupOverageClaimType = "group_overage";

    public AuthorizationDecision Resolve(ClaimsPrincipal principal, Permission permission)
    {
        var normalizedPrincipal = new NormalizedPrincipal(
            new ExternalPrincipalKey(
                principal.Identity?.AuthenticationType ?? "unknown",
                "urn:datapitcher:validated-token",
                null,
                PrincipalKind.User,
                Subject(principal)
            ),
            new(null, null, null, null)
        );
        var grants = principal
            .FindAll(RolesClaimType)
            .Select(claim => claim.Value)
            .Where(value => Enum.TryParse<Role>(value, out _))
            .Select(value => new RoleGrant(Enum.Parse<Role>(value), RoleGrantSource.OpenIdConnectRole))
            .ToArray();
        var groupResolution = GroupResolution(principal);
        var indeterminateRoles =
            groupResolution.State == GroupResolutionState.Indeterminate ? Enum.GetValues<Role>() : [];
        var input = new AuthorizationInput(
            normalizedPrincipal,
            PrincipalAuthorizationState.Active,
            grants,
            groupResolution,
            indeterminateRoles
        );
        return AuthorizationEvaluator.Evaluate(input, permission);
    }

    private static string Subject(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value ?? "unspecified";

    private static GroupResolutionResult GroupResolution(ClaimsPrincipal principal) =>
        principal.HasClaim(claim =>
            string.Equals(claim.Type, GroupOverageClaimType, StringComparison.Ordinal)
            && string.Equals(claim.Value, "true", StringComparison.Ordinal)
        )
            ? GroupResolutionResult.Indeterminate()
            : GroupResolutionResult.Complete(principal.FindAll(GroupsClaimType).Select(claim => claim.Value));
}
