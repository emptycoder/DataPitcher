namespace DataPitcher.Core.Jobs;

public enum JobState
{
    Draft,
    Queued,
    Preparing,
    Running,
    Pausing,
    Paused,
    Cancelling,
    Cancelled,
    Verifying,
    Succeeded,
    Failed,
    VerificationFailed,
}

public sealed class InvalidJobStateTransitionException(JobState from, JobState to)
    : InvalidOperationException($"Job cannot transition from {from} to {to}.");

public static class JobStateMachine
{
    public static IReadOnlyList<JobState> ValidTargets(JobState from) =>
        from switch
        {
            JobState.Draft => [JobState.Queued, JobState.Cancelling],
            JobState.Queued => [JobState.Preparing, JobState.Cancelling],
            JobState.Preparing =>
            [
                JobState.Running,
                JobState.Pausing,
                JobState.Cancelling,
                JobState.Failed,
                JobState.Queued,
            ],
            JobState.Running =>
            [
                JobState.Pausing,
                JobState.Cancelling,
                JobState.Verifying,
                JobState.Failed,
                JobState.Queued,
            ],
            JobState.Pausing => [JobState.Paused, JobState.Cancelling, JobState.Failed, JobState.Queued],
            JobState.Paused => [JobState.Queued, JobState.Cancelling],
            JobState.Cancelling => [JobState.Cancelled, JobState.Failed],
            JobState.Verifying => [JobState.Succeeded, JobState.Failed, JobState.VerificationFailed],
            _ => [],
        };

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!ValidTargets(from).Contains(to))
            throw new InvalidJobStateTransitionException(from, to);
    }
}
