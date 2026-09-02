using DataPitcher.Core.Jobs;
using Xunit;

namespace DataPitcher.UnitTests.Jobs;

public sealed class JobStateMachineTests
{
    private static readonly HashSet<(JobState From, JobState To)> Allowed =
    [
        (JobState.Draft, JobState.Queued), (JobState.Draft, JobState.Cancelling),
        (JobState.Queued, JobState.Preparing), (JobState.Queued, JobState.Cancelling),
        (JobState.Preparing, JobState.Running), (JobState.Preparing, JobState.Pausing), (JobState.Preparing, JobState.Cancelling), (JobState.Preparing, JobState.Failed), (JobState.Preparing, JobState.Queued),
        (JobState.Running, JobState.Pausing), (JobState.Running, JobState.Cancelling), (JobState.Running, JobState.Verifying), (JobState.Running, JobState.Failed), (JobState.Running, JobState.Queued),
        (JobState.Pausing, JobState.Paused), (JobState.Pausing, JobState.Cancelling), (JobState.Pausing, JobState.Failed), (JobState.Pausing, JobState.Queued),
        (JobState.Paused, JobState.Queued), (JobState.Paused, JobState.Cancelling),
        (JobState.Cancelling, JobState.Cancelled), (JobState.Cancelling, JobState.Failed),
        (JobState.Verifying, JobState.Succeeded), (JobState.Verifying, JobState.Failed), (JobState.Verifying, JobState.VerificationFailed),
    ];
    public static IEnumerable<object[]> StatePairs() => Enum.GetValues<JobState>().SelectMany(from => Enum.GetValues<JobState>().Select(to => new object[] { from, to }));

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void JobStateMachine_ForEveryStatePair_AcceptsOnlyTheSpecifiedTransitions(JobState from, JobState to)
    {
        Assert.Equal(Allowed.Where(pair => pair.From == from).Select(pair => pair.To), JobStateMachine.ValidTargets(from));
        if (Allowed.Contains((from, to)))
            JobStateMachine.EnsureTransition(from, to);
        else
            Assert.Throws<InvalidJobStateTransitionException>(() => JobStateMachine.EnsureTransition(from, to));
    }
}
