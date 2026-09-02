using DataPitcher.Auth.Abstractions.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class GroupResolutionResultTests
{
    [Fact]
    public void GroupResolutionResult_NotApplicable_HasItsOwnStateAndNoGroups()
    {
        var result = GroupResolutionResult.NotApplicable();
        Assert.Equal(GroupResolutionState.NotApplicable, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Complete_PreservesKnownEmptyMembership()
    {
        var result = GroupResolutionResult.Complete([]);
        Assert.Equal(GroupResolutionState.Complete, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Complete_DefensivelyCopiesImmutableGroupIdentifiers()
    {
        var identifiers = new List<string> { "group-b", "group-a", "group-a" };
        var result = GroupResolutionResult.Complete(identifiers);
        identifiers.Clear();
        Assert.Equal(["group-a", "group-b"], result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Indeterminate_DiscardsAnyMembershipAndIsNotComplete()
    {
        var result = GroupResolutionResult.Indeterminate();
        Assert.Equal(GroupResolutionState.Indeterminate, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }
}
