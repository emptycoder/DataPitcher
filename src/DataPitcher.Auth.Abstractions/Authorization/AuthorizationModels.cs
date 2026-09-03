using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;

namespace DataPitcher.Auth.Abstractions.Authorization;

public enum RoleGrantSource
{
    ControlDatabaseAssignment,
    ApplicationRole,
    DirectoryGroup,
    OpenIdConnectRole,
    OpenIdConnectGroup,
}

public sealed record RoleGrant(Role Role, RoleGrantSource Source);

public enum PrincipalAuthorizationState
{
    Active,
    TerminalDeny,
    Disabled,
}

public enum AuthorizationOutcome
{
    Granted,
    Denied,
    Indeterminate,
}

public sealed record AuthorizationDecision(AuthorizationOutcome Outcome, PermissionSet EffectivePermissions);

public sealed record PrincipalStateChange(
    ExternalPrincipalKey Principal,
    PrincipalAuthorizationState PreviousState,
    PrincipalAuthorizationState CurrentState,
    ExternalPrincipalKey ChangedBy,
    DateTimeOffset OccurredAt
)
{
    public bool RequiresAudit =>
        (PreviousState is PrincipalAuthorizationState.TerminalDeny or PrincipalAuthorizationState.Disabled)
        && CurrentState == PrincipalAuthorizationState.Active;
}

public sealed class AuthorizationInput
{
    public AuthorizationInput(
        NormalizedPrincipal principal,
        PrincipalAuthorizationState principalState,
        IEnumerable<RoleGrant> positiveGrants,
        GroupResolutionResult groupResolution,
        IEnumerable<Role> rolesThatMayBeGrantedByIndeterminateGroups
    )
    {
        Principal = principal;
        PrincipalState = principalState;
        PositiveGrants = Array.AsReadOnly(positiveGrants.ToArray());
        GroupResolution = groupResolution;
        RolesThatMayBeGrantedByIndeterminateGroups = Array.AsReadOnly(
            rolesThatMayBeGrantedByIndeterminateGroups.Distinct().ToArray()
        );
    }

    public NormalizedPrincipal Principal { get; }
    public PrincipalAuthorizationState PrincipalState { get; }
    public IReadOnlyCollection<RoleGrant> PositiveGrants { get; }
    public GroupResolutionResult GroupResolution { get; }
    public IReadOnlyCollection<Role> RolesThatMayBeGrantedByIndeterminateGroups { get; }
}
