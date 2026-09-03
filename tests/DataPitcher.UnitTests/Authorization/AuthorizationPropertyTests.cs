using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class AuthorizationPropertyTests
{
    [Property(MaxTest = 100)]
    public void AuthorizationEvaluator_IsDeterministicWhenGrantsAreReversed(Role[] roles)
    {
        var forward = AuthorizationEvaluator.Evaluate(Input(roles), Permissions.TransfersStart);
        var reversed = AuthorizationEvaluator.Evaluate(Input(roles.Reverse()), Permissions.TransfersStart);

        Assert.Equal(forward, reversed);
    }

    [Property(MaxTest = 100)]
    public void AuthorizationEvaluator_WhenAnyPositiveGrantIsRemoved_CanOnlyShrinkEffectivePermissions(
        Role[] roles,
        NonNegativeInt index
    )
    {
        var full = AuthorizationEvaluator.Evaluate(Input(roles), Permissions.TransfersStart).EffectivePermissions;
        var reduced = AuthorizationEvaluator
            .Evaluate(
                Input(roles.Where((_, position) => roles.Length == 0 || position != index.Get % roles.Length)),
                Permissions.TransfersStart
            )
            .EffectivePermissions;

        Assert.True(reduced.IsSubsetOf(full));
    }

    [Property(MaxTest = 100)]
    public void PermissionSet_WhenAPermissionIsRemoved_CannotGainAccess(Role[] roles, NonNegativeInt index)
    {
        var before = AuthorizationEvaluator.Evaluate(Input(roles), Permissions.TransfersStart).EffectivePermissions;
        var after =
            before.Permissions.Count == 0
                ? before
                : before.Without(before.Permissions.ElementAt(index.Get % before.Permissions.Count));

        Assert.True(after.IsSubsetOf(before));
    }

    private static AuthorizationInput Input(IEnumerable<Role> roles) =>
        new(
            new(
                new("provider", "https://issuer.example", null, PrincipalKind.User, "subject"),
                new(null, null, null, null)
            ),
            PrincipalAuthorizationState.Active,
            roles.Select(role => new RoleGrant(role, RoleGrantSource.ControlDatabaseAssignment)),
            GroupResolutionResult.NotApplicable(),
            []
        );
}
