using System.Globalization;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Worker;

namespace DataPitcher.Infrastructure.Persistence;

public sealed record TransferJob(Guid JobId, Guid RunId, Guid PlanId, string IdempotencyKey, JobState State, string? FailureCode = null, DateTimeOffset CreatedUtc = default, DateTimeOffset UpdatedUtc = default);
public sealed record StartJobRequest(Guid PlanId, string IdempotencyKey);
public sealed record StartJobResult(TransferJob Job, bool Created);
public sealed record JobTransitionResult(TransferJob? Job, int RowsAffected);

public sealed class JobStore(ControlDatabase database, IClock clock) : IJobControl
{
    private readonly LeaseStore _leases = new(database, clock);

    public StartJobResult Start(StartJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(request));
        using var db = database.Open(); using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow); var row = new JobRow { JobId = Guid.NewGuid().ToString(), RunId = Guid.NewGuid().ToString(), PlanId = request.PlanId.ToString(), IdempotencyKey = request.IdempotencyKey, State = JobState.Queued.ToString(), CreatedUtc = now, UpdatedUtc = now };
        var inserted = db.Execute("INSERT OR IGNORE INTO Jobs (JobId, RunId, PlanId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES (@jobId, @runId, @planId, @idempotencyKey, @state, @createdUtc, @updatedUtc)", new DataParameter("jobId", row.JobId), new DataParameter("runId", row.RunId), new DataParameter("planId", row.PlanId), new DataParameter("idempotencyKey", row.IdempotencyKey), new DataParameter("state", row.State), new DataParameter("createdUtc", row.CreatedUtc), new DataParameter("updatedUtc", row.UpdatedUtc));
        if (inserted == 0) return new(ToJob(db.GetTable<JobRow>().Single(existing => existing.IdempotencyKey == request.IdempotencyKey)), false);
        PersistHistory(db, Guid.Parse(row.JobId), JobState.Draft, JobState.Queued, now); db.Insert(new JobLeaseRow { JobId = row.JobId, FenceToken = 0 }); transaction.Commit();
        return new(ToJob(row), true);
    }

    public async Task<JobClaim?> TryClaimNextAsync(string ownerId, TimeSpan leaseTtl, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        var candidates = await db.GetTable<JobRow>().Where(row => row.State == JobState.Queued.ToString() || row.State == JobState.Preparing.ToString() || row.State == JobState.Running.ToString() || row.State == JobState.Pausing.ToString()).OrderBy(row => row.UpdatedUtc).ToArrayAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            var lease = await _leases.AcquireAsync(Guid.Parse(candidate.JobId), ownerId, leaseTtl, cancellationToken);
            if (lease is not null) return new(ToJob(candidate), lease, candidate.State != JobState.Queued.ToString());
        }
        return null;
    }

    public async Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        var row = await db.GetTable<JobRow>().Where(row => row.JobId == jobId.ToString()).SingleAsync(cancellationToken);
        return Enum.Parse<JobState>(row.State);
    }

    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) => TransitionOperatorIntentAsync(jobId, JobState.Pausing, cancellationToken);
    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) => TransitionOperatorIntentAsync(jobId, JobState.Queued, cancellationToken);
    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) => TransitionOperatorIntentAsync(jobId, JobState.Cancelling, cancellationToken);

    public async Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken)
    {
        if (claim.IsInterrupted) await TransitionWorkerAsync(claim.Lease, JobState.Queued, null, false, cancellationToken);
        await TransitionWorkerAsync(claim.Lease, JobState.Preparing, null, false, cancellationToken);
    }

    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Running, null, false, cancellationToken);
    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Paused, null, true, cancellationToken);
    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Cancelled, null, true, cancellationToken);
    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Verifying, null, false, cancellationToken);
    public Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Succeeded, null, true, cancellationToken);
    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) => TransitionWorkerAsync(lease, JobState.Failed, failureCode, true, cancellationToken);

    public JobTransitionResult TryTransition(LeaseGrant lease, JobState to)
    {
        using var db = database.Open(); using var transaction = db.BeginTransaction(); var row = db.GetTable<JobRow>().Single(row => row.JobId == lease.JobId.ToString()); var from = Enum.Parse<JobState>(row.State); JobStateMachine.EnsureTransition(from, to); var now = Stamp(clock.UtcNow);
        var affected = db.Execute("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)", new DataParameter("toState", to.ToString()), new DataParameter("nowUtc", now), new DataParameter("jobId", lease.JobId.ToString()), new DataParameter("fromState", from.ToString()), new DataParameter("ownerId", lease.OwnerId), new DataParameter("fenceToken", lease.FenceToken));
        if (affected == 0) return new(null, 0);
        PersistHistory(db, lease.JobId, from, to, now); row.State = to.ToString(); row.UpdatedUtc = now; transaction.Commit(); return new(ToJob(row), 1);
    }

    public TransferJob Get(Guid jobId) => ToJob(database.Open().GetTable<JobRow>().Single(row => row.JobId == jobId.ToString()));
    public TransferJob? Find(Guid jobId) { var row = database.Open().GetTable<JobRow>().SingleOrDefault(item => item.JobId == jobId.ToString()); return row is null ? null : ToJob(row); }
    public IReadOnlyList<TransferJob> List(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return database.Open().GetTable<JobRow>().ToArray().OrderByDescending(row => row.CreatedUtc, StringComparer.Ordinal).ThenBy(row => row.JobId, StringComparer.Ordinal).Select(ToJob).ToArray(); }
    public IReadOnlyList<(JobState From, JobState To)> GetHistory(Guid jobId) => database.Open().GetTable<JobStateTransitionRow>().Where(row => row.JobId == jobId.ToString()).OrderBy(row => row.OccurredUtc).Select(row => new { row.FromState, row.ToState }).AsEnumerable().Select(row => (Enum.Parse<JobState>(row.FromState), Enum.Parse<JobState>(row.ToState))).ToArray();

    private async Task TransitionOperatorIntentAsync(Guid jobId, JobState to, CancellationToken cancellationToken)
    {
        using var db = database.Open(); using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var row = await db.GetTable<JobRow>().Where(row => row.JobId == jobId.ToString()).SingleAsync(cancellationToken); var from = Enum.Parse<JobState>(row.State); JobStateMachine.EnsureTransition(from, to); var now = Stamp(clock.UtcNow);
        var affected = await db.ExecuteAsync("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState", cancellationToken, new DataParameter[] { new("toState", to.ToString()), new("nowUtc", now), new("jobId", jobId.ToString()), new("fromState", from.ToString()) });
        if (affected != 1) throw new InvalidOperationException("Job control intent was superseded.");
        await PersistHistoryAsync(db, jobId, from, to, now, cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    private async Task TransitionWorkerAsync(LeaseGrant lease, JobState to, string? failureCode, bool releaseLease, CancellationToken cancellationToken)
    {
        using var db = database.Open(); using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var row = await db.GetTable<JobRow>().Where(row => row.JobId == lease.JobId.ToString()).SingleAsync(cancellationToken); var from = Enum.Parse<JobState>(row.State); JobStateMachine.EnsureTransition(from, to); var now = Stamp(clock.UtcNow);
        var failure = failureCode is null ? "" : ", FailureCode = @failureCode";
        var affected = await db.ExecuteAsync($"UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc{failure} WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)", cancellationToken, new DataParameter[] { new("toState", to.ToString()), new("nowUtc", now), new("failureCode", failureCode), new("jobId", lease.JobId.ToString()), new("fromState", from.ToString()), new("ownerId", lease.OwnerId), new("fenceToken", lease.FenceToken) });
        if (affected != 1) throw new InvalidOperationException("Worker no longer owns the job.");
        if (releaseLease)
        {
            var released = await db.ExecuteAsync("UPDATE JobLeases SET OwnerId = NULL, ExpiresUtc = NULL WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc", cancellationToken, new DataParameter[] { new("jobId", lease.JobId.ToString()), new("ownerId", lease.OwnerId), new("fenceToken", lease.FenceToken), new("nowUtc", now) });
            if (released != 1) throw new InvalidOperationException("Worker lease release was superseded.");
        }
        await PersistHistoryAsync(db, lease.JobId, from, to, now, cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    private static void PersistHistory(DataConnection db, Guid jobId, JobState from, JobState to, string now) => db.Insert(new JobStateTransitionRow { TransitionId = Guid.NewGuid().ToString(), JobId = jobId.ToString(), FromState = from.ToString(), ToState = to.ToString(), OccurredUtc = now });
    private static Task<int> PersistHistoryAsync(DataConnection db, Guid jobId, JobState from, JobState to, string now, CancellationToken cancellationToken) => db.ExecuteAsync("INSERT INTO JobStateTransitions (TransitionId, JobId, FromState, ToState, OccurredUtc) VALUES (@transitionId, @jobId, @fromState, @toState, @occurredUtc)", cancellationToken, new DataParameter[] { new("transitionId", Guid.NewGuid().ToString()), new("jobId", jobId.ToString()), new("fromState", from.ToString()), new("toState", to.ToString()), new("occurredUtc", now) });
    private static TransferJob ToJob(JobRow row) => new(Guid.Parse(row.JobId), Guid.Parse(row.RunId), Guid.Parse(row.PlanId), row.IdempotencyKey, Enum.Parse<JobState>(row.State), row.FailureCode, DateTimeOffset.Parse(row.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(row.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
