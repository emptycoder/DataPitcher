using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class AuthorizationEvaluatorTests
{
    [Theory]
    [InlineData(PrincipalAuthorizationState.TerminalDeny)]
    [InlineData(PrincipalAuthorizationState.Disabled)]
    public void AuthorizationEvaluator_WhenPrincipalIsTerminal_ReturnsDeniedBeforeGrantUnion(PrincipalAuthorizationState state)
    {
        var decision = AuthorizationEvaluator.Evaluate(Input(state, [new(Role.Administrator, RoleGrantSource.ControlDatabaseAssignment)]), Permissions.AuthProvidersManage);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal(PermissionSet.Empty, decision.EffectivePermissions);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenPositiveSourcesGrantDifferentRoles_UsesTheirUnion()
    {
        var grants = new[]
        {
            new RoleGrant(Role.Planner, RoleGrantSource.ApplicationRole),
            new RoleGrant(Role.Operator, RoleGrantSource.DirectoryGroup),
            new RoleGrant(Role.Administrator, RoleGrantSource.OpenIdConnectRole),
            new RoleGrant(Role.Viewer, RoleGrantSource.OpenIdConnectGroup),
            new RoleGrant(Role.Viewer, RoleGrantSource.ControlDatabaseAssignment),
        };

        var decision = AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, grants), Permissions.AuthProvidersManage);

        Assert.Equal(AuthorizationOutcome.Granted, decision.Outcome);
        Assert.Equal(new PermissionSet(Permissions.All), decision.EffectivePermissions);
    }

    [Theory]
    [InlineData(GroupResolutionState.NotApplicable)]
    [InlineData(GroupResolutionState.Complete)]
    public void AuthorizationEvaluator_WhenAllRelevantSourcesAreComplete_ReturnsDenied(GroupResolutionState state)
    {
        var groups = state == GroupResolutionState.Complete ? GroupResolutionResult.Complete([]) : GroupResolutionResult.NotApplicable();

        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], groups, [Role.Operator]), Permissions.TransfersStart).Outcome);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenIndeterminateGroupsCouldGrantRequestedPermission_ReturnsIndeterminate() =>
        Assert.Equal(AuthorizationOutcome.Indeterminate, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], GroupResolutionResult.Indeterminate(), [Role.Operator]), Permissions.TransfersStart).Outcome);

    [Fact]
    public void AuthorizationEvaluator_WhenIndeterminateGroupsCannotGrantRequestedPermission_ReturnsDenied() =>
        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], GroupResolutionResult.Indeterminate(), [Role.Planner]), Permissions.TransfersStart).Outcome);

    [Fact]
    public void AuthorizationEvaluator_WhenIndeterminateGroupsOnlyHaveUnrecognizedRoles_ReturnsDenied() =>
        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], GroupResolutionResult.Indeterminate(), [(Role)99]), Permissions.TransfersStart).Outcome);

    [Fact]
    public void AuthorizationEvaluator_WhenKnownGrantExists_DoesNotTurnItIntoIndeterminate()
    {
        var decision = AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [new(Role.Operator, RoleGrantSource.ControlDatabaseAssignment)], GroupResolutionResult.Indeterminate(), [Role.Planner]), Permissions.TransfersStart);

        Assert.Equal(AuthorizationOutcome.Granted, decision.Outcome);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenGrantRoleIsUnrecognized_GrantsNothing()
    {
        var decision = AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [new((Role)99, RoleGrantSource.ControlDatabaseAssignment)]), Permissions.TransfersStart);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal(PermissionSet.Empty, decision.EffectivePermissions);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenPresentationClaimsChange_UsesOnlyTheImmutableAuthorizationKey()
    {
        var key = new ExternalPrincipalKey("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject");
        var grants = new[] { new RoleGrant(Role.Operator, RoleGrantSource.ControlDatabaseAssignment) };
        var first = Input(PrincipalAuthorizationState.Active, grants, principal: new(key, new("Ada", "ada@example.test", "ada", "ada@example.test")));
        var renamed = Input(PrincipalAuthorizationState.Active, grants, principal: new(key, new("Grace", "grace@example.test", "grace", "grace@example.test")));

        Assert.Equal(AuthorizationEvaluator.Evaluate(first, Permissions.TransfersStart), AuthorizationEvaluator.Evaluate(renamed, Permissions.TransfersStart));
    }

    [Fact]
    public void PrincipalStateChange_WhenDisabledStateIsLifted_IsAuditableAndNotAGrantRemoval()
    {
        var disabled = Input(PrincipalAuthorizationState.Disabled, [new(Role.Operator, RoleGrantSource.ControlDatabaseAssignment)]);
        var enabled = Input(PrincipalAuthorizationState.Active, disabled.PositiveGrants);
        var change = new PrincipalStateChange(disabled.Principal.AuthorizationKey, PrincipalAuthorizationState.Disabled, PrincipalAuthorizationState.Active, disabled.Principal.AuthorizationKey, DateTimeOffset.UnixEpoch);

        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(disabled, Permissions.TransfersStart).Outcome);
        Assert.True(change.RequiresAudit);
        Assert.Equal(disabled.PositiveGrants, enabled.PositiveGrants);
        Assert.Equal(AuthorizationOutcome.Granted, AuthorizationEvaluator.Evaluate(enabled, Permissions.TransfersStart).Outcome);
    }

    [Fact]
    public void PrincipalStateChange_WhenTerminalDenyIsLifted_RequiresAudit()
    {
        var key = new ExternalPrincipalKey("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject");
        var change = new PrincipalStateChange(key, PrincipalAuthorizationState.TerminalDeny, PrincipalAuthorizationState.Active, key, DateTimeOffset.UnixEpoch);

        Assert.True(change.RequiresAudit);
    }

    [Fact]
    public void PrincipalStateChange_WhenTerminalStateChangesWithoutBeingLifted_DoesNotRequireAudit()
    {
        var key = new ExternalPrincipalKey("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject");
        var change = new PrincipalStateChange(key, PrincipalAuthorizationState.TerminalDeny, PrincipalAuthorizationState.Disabled, key, DateTimeOffset.UnixEpoch);

        Assert.False(change.RequiresAudit);
    }

    [Fact]
    public void PrincipalStateChange_WhenActivePrincipalIsDisabled_DoesNotRequireAudit()
    {
        var key = new ExternalPrincipalKey("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject");
        var change = new PrincipalStateChange(key, PrincipalAuthorizationState.Active, PrincipalAuthorizationState.Disabled, key, DateTimeOffset.UnixEpoch);

        Assert.False(change.RequiresAudit);
    }

    private static AuthorizationInput Input(PrincipalAuthorizationState state, IEnumerable<RoleGrant> grants, GroupResolutionResult? groups = null, IEnumerable<Role>? possibleRoles = null, NormalizedPrincipal? principal = null) =>
        new(principal ?? new(new("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject"), new(null, null, null, null)), state, grants, groups ?? GroupResolutionResult.NotApplicable(), possibleRoles ?? []);
}
