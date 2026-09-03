namespace DataPitcher.Core.Jobs;

public sealed record TransferJob(
    Guid JobId,
    Guid RunId,
    Guid PlanId,
    string IdempotencyKey,
    JobState State,
    string? FailureCode = null,
    DateTimeOffset CreatedUtc = default,
    DateTimeOffset UpdatedUtc = default,
    string? FailureDetail = null
);

public sealed record StartJobRequest(Guid PlanId, string IdempotencyKey);

public sealed record StartJobResult(TransferJob Job, bool Created);

public sealed record JobTransitionResult(TransferJob? Job, int RowsAffected);

public sealed record JobClaim(TransferJob Job, LeaseGrant Lease, bool IsInterrupted);

public interface IJobControl
{
    Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken);
    Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken);
    Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken);
    Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken);
    Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken);
    Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken);

    /// <summary>Marks the job failed with a fixed code and an operator-readable reason (secrets removed).</summary>
    Task MarkFailedAsync(
        LeaseGrant lease,
        string failureCode,
        string? failureDetail,
        CancellationToken cancellationToken
    ) => MarkFailedAsync(lease, failureCode, cancellationToken);

    /// <summary>The batches committed but post-transfer verification found the target does not match the plan.</summary>
    Task MarkVerificationFailedAsync(LeaseGrant lease, string? failureDetail, CancellationToken cancellationToken) =>
        MarkFailedAsync(lease, "verification_failed", failureDetail, cancellationToken);
}

/// <summary>Job persistence beyond worker control: starting, listing and inspecting jobs.</summary>
public interface IJobRepository : IJobControl
{
    StartJobResult Start(StartJobRequest request);

    JobTransitionResult TryTransition(LeaseGrant lease, JobState to);

    TransferJob Get(Guid jobId);

    TransferJob? Find(Guid jobId);

    IReadOnlyList<TransferJob> List(CancellationToken cancellationToken);

    IReadOnlyList<(JobState From, JobState To)> GetHistory(Guid jobId);
}
