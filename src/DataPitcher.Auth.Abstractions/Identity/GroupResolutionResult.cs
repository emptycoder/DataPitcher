namespace DataPitcher.Auth.Abstractions.Identity;

public enum GroupResolutionState { NotApplicable, Complete, Indeterminate }

public sealed class GroupResolutionResult
{
    private GroupResolutionResult(GroupResolutionState state, IEnumerable<string> immutableGroupIds)
    {
        State = state;
        ImmutableGroupIds = Array.AsReadOnly(immutableGroupIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
    public GroupResolutionState State { get; }
    public IReadOnlyCollection<string> ImmutableGroupIds { get; }
    public static GroupResolutionResult NotApplicable() => new(GroupResolutionState.NotApplicable, []);
    public static GroupResolutionResult Complete(IEnumerable<string> immutableGroupIds) => new(GroupResolutionState.Complete, immutableGroupIds);
    public static GroupResolutionResult Indeterminate() => new(GroupResolutionState.Indeterminate, []);
}
