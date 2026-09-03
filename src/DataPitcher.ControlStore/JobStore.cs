using System.Globalization;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Time;
using Microsoft.Data.Sqlite;

namespace DataPitcher.ControlStore;

public sealed class JobStore(ControlDatabase database, IClock clock) : IJobRepository
{
    private const string SelectJob =
        "SELECT JobId, RunId, PlanId, IdempotencyKey, State, CreatedUtc, UpdatedUtc, FailureCode, FailureDetail FROM Jobs";

    private const string NoElements = "Sequence contains no elements";

    private readonly LeaseStore _leases = new(database, clock);

    public StartJobResult Start(StartJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var inserted = db.Execute(
            "INSERT OR IGNORE INTO Jobs (JobId, RunId, PlanId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES (@jobId, @runId, @planId, @idempotencyKey, @state, @createdUtc, @updatedUtc)",
            new ControlParameter("jobId", jobId.ToString()),
            new ControlParameter("runId", runId.ToString()),
            new ControlParameter("planId", request.PlanId.ToString()),
            new ControlParameter("idempotencyKey", request.IdempotencyKey),
            new ControlParameter("state", JobState.Queued.ToString()),
            new ControlParameter("createdUtc", now),
            new ControlParameter("updatedUtc", now)
        );
        if (inserted == 0)
            return new(
                Require(
                    db.Single(
                        SelectJob + " WHERE IdempotencyKey = @idempotencyKey",
                        ReadJob,
                        new ControlParameter("idempotencyKey", request.IdempotencyKey)
                    )
                ),
                false
            );
        PersistHistory(db, jobId, JobState.Draft, JobState.Queued, now);
        db.Execute(
            "INSERT INTO JobLeases (JobId, OwnerId, ExpiresUtc, FenceToken) VALUES (@jobId, NULL, NULL, 0)",
            new ControlParameter("jobId", jobId.ToString())
        );
        transaction.Commit();
        var created = ParseStamp(now);
        return new(
            new TransferJob(
                jobId,
                runId,
                request.PlanId,
                request.IdempotencyKey,
                JobState.Queued,
                null,
                created,
                created
            ),
            true
        );
    }

    public async Task<JobClaim?> TryClaimNextAsync(
        string ownerId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken
    )
    {
        using var db = database.Open();
        var candidates = await db.QueryAsync(
            SelectJob + " WHERE State IN (@queued, @preparing, @running, @pausing) ORDER BY UpdatedUtc",
            ReadJob,
            cancellationToken,
            new ControlParameter("queued", JobState.Queued.ToString()),
            new ControlParameter("preparing", JobState.Preparing.ToString()),
            new ControlParameter("running", JobState.Running.ToString()),
            new ControlParameter("pausing", JobState.Pausing.ToString())
        );
        foreach (var candidate in candidates)
        {
            var lease = await _leases.AcquireAsync(candidate.JobId, ownerId, leaseTtl, cancellationToken);
            if (lease is not null)
                return new(candidate, lease, candidate.State != JobState.Queued);
        }
        return null;
    }

    public async Task<JobState> GetStateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        var job = await FindAsync(db, jobId, cancellationToken);
        return Require(job).State;
    }

    public Task RequestPauseAsync(Guid jobId, CancellationToken cancellationToken) =>
        TransitionOperatorIntentAsync(jobId, JobState.Pausing, cancellationToken);

    public Task RequestResumeAsync(Guid jobId, CancellationToken cancellationToken) =>
        TransitionOperatorIntentAsync(jobId, JobState.Queued, cancellationToken);

    public Task RequestCancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        TransitionOperatorIntentAsync(jobId, JobState.Cancelling, cancellationToken);

    public async Task PrepareAsync(JobClaim claim, CancellationToken cancellationToken)
    {
        if (claim.IsInterrupted)
            await TransitionWorkerAsync(claim.Lease, JobState.Queued, null, false, cancellationToken);
        await TransitionWorkerAsync(claim.Lease, JobState.Preparing, null, false, cancellationToken);
    }

    public Task MarkRunningAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Running, null, false, cancellationToken);

    public Task MarkPausedAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Paused, null, true, cancellationToken);

    public Task MarkCancelledAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Cancelled, null, true, cancellationToken);

    public Task MarkVerifyingAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Verifying, null, false, cancellationToken);

    public Task MarkSucceededAsync(LeaseGrant lease, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Succeeded, null, true, cancellationToken);

    public Task MarkFailedAsync(LeaseGrant lease, string failureCode, CancellationToken cancellationToken) =>
        TransitionWorkerAsync(lease, JobState.Failed, failureCode, true, cancellationToken);

    public Task MarkFailedAsync(
        LeaseGrant lease,
        string failureCode,
        string? failureDetail,
        CancellationToken cancellationToken
    ) => TransitionWorkerAsync(lease, JobState.Failed, failureCode, true, cancellationToken, failureDetail);

    public Task MarkVerificationFailedAsync(
        LeaseGrant lease,
        string? failureDetail,
        CancellationToken cancellationToken
    ) =>
        TransitionWorkerAsync(
            lease,
            JobState.VerificationFailed,
            "verification_failed",
            true,
            cancellationToken,
            failureDetail
        );

    public JobTransitionResult TryTransition(LeaseGrant lease, JobState to)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var job = Require(Find(db, lease.JobId));
        var from = job.State;
        JobStateMachine.EnsureTransition(from, to);
        var now = Stamp(clock.UtcNow);
        var affected = db.Execute(
            "UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)",
            new ControlParameter("toState", to.ToString()),
            new ControlParameter("nowUtc", now),
            new ControlParameter("jobId", lease.JobId.ToString()),
            new ControlParameter("fromState", from.ToString()),
            new ControlParameter("ownerId", lease.OwnerId),
            new ControlParameter("fenceToken", lease.FenceToken)
        );
        if (affected == 0)
            return new(null, 0);
        PersistHistory(db, lease.JobId, from, to, now);
        transaction.Commit();
        return new(job with { State = to, UpdatedUtc = ParseStamp(now) }, 1);
    }

    public TransferJob Get(Guid jobId)
    {
        using var db = database.Open();
        return Require(Find(db, jobId));
    }

    public TransferJob? Find(Guid jobId)
    {
        using var db = database.Open();
        return Find(db, jobId);
    }

    public IReadOnlyList<TransferJob> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        return db.Query(SelectJob + " ORDER BY CreatedUtc DESC, JobId ASC", ReadJob);
    }

    public IReadOnlyList<(JobState From, JobState To)> GetHistory(Guid jobId)
    {
        using var db = database.Open();
        return db.Query(
            "SELECT FromState, ToState FROM JobStateTransitions WHERE JobId = @jobId ORDER BY OccurredUtc",
            reader => (Enum.Parse<JobState>(reader.GetString(0)), Enum.Parse<JobState>(reader.GetString(1))),
            new ControlParameter("jobId", jobId.ToString())
        );
    }

    private async Task TransitionOperatorIntentAsync(Guid jobId, JobState to, CancellationToken cancellationToken)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var job = Require(await FindAsync(db, jobId, cancellationToken));
        var from = job.State;
        JobStateMachine.EnsureTransition(from, to);
        var now = Stamp(clock.UtcNow);
        var affected = await db.ExecuteAsync(
            "UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState",
            cancellationToken,
            new ControlParameter("toState", to.ToString()),
            new ControlParameter("nowUtc", now),
            new ControlParameter("jobId", jobId.ToString()),
            new ControlParameter("fromState", from.ToString())
        );
        if (affected != 1)
            throw new InvalidOperationException("Job control intent was superseded.");
        await PersistHistoryAsync(db, jobId, from, to, now, cancellationToken);
        transaction.Commit();
    }

    private async Task TransitionWorkerAsync(
        LeaseGrant lease,
        JobState to,
        string? failureCode,
        bool releaseLease,
        CancellationToken cancellationToken,
        string? failureDetail = null
    )
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var job = Require(await FindAsync(db, lease.JobId, cancellationToken));
        var from = job.State;
        JobStateMachine.EnsureTransition(from, to);
        var now = Stamp(clock.UtcNow);
        var failure = failureCode is null ? "" : ", FailureCode = @failureCode, FailureDetail = @failureDetail";
        var affected = await db.ExecuteAsync(
            $"UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc{failure} WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)",
            cancellationToken,
            new ControlParameter("toState", to.ToString()),
            new ControlParameter("nowUtc", now),
            new ControlParameter("failureCode", failureCode),
            new ControlParameter("failureDetail", failureDetail),
            new ControlParameter("jobId", lease.JobId.ToString()),
            new ControlParameter("fromState", from.ToString()),
            new ControlParameter("ownerId", lease.OwnerId),
            new ControlParameter("fenceToken", lease.FenceToken)
        );
        if (affected != 1)
            throw new InvalidOperationException("Worker no longer owns the job.");
        if (releaseLease)
        {
            var released = await db.ExecuteAsync(
                "UPDATE JobLeases SET OwnerId = NULL, ExpiresUtc = NULL WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc",
                cancellationToken,
                new ControlParameter("jobId", lease.JobId.ToString()),
                new ControlParameter("ownerId", lease.OwnerId),
                new ControlParameter("fenceToken", lease.FenceToken),
                new ControlParameter("nowUtc", now)
            );
            if (released != 1)
                throw new InvalidOperationException("Worker lease release was superseded.");
        }
        await PersistHistoryAsync(db, lease.JobId, from, to, now, cancellationToken);
        transaction.Commit();
    }

    private static TransferJob? Find(ControlConnection db, Guid jobId) =>
        db.Single(SelectJob + " WHERE JobId = @jobId", ReadJob, new ControlParameter("jobId", jobId.ToString()));

    private static async Task<TransferJob?> FindAsync(
        ControlConnection db,
        Guid jobId,
        CancellationToken cancellationToken
    )
    {
        var rows = await db.QueryAsync(
            SelectJob + " WHERE JobId = @jobId",
            ReadJob,
            cancellationToken,
            new ControlParameter("jobId", jobId.ToString())
        );
        return rows.Count == 0 ? null : rows[0];
    }

    private static TransferJob Require(TransferJob? job) => job ?? throw new InvalidOperationException(NoElements);

    private static void PersistHistory(ControlConnection db, Guid jobId, JobState from, JobState to, string now) =>
        db.Execute(
            "INSERT INTO JobStateTransitions (TransitionId, JobId, FromState, ToState, OccurredUtc) VALUES (@transitionId, @jobId, @fromState, @toState, @occurredUtc)",
            HistoryParameters(jobId, from, to, now)
        );

    private static Task<int> PersistHistoryAsync(
        ControlConnection db,
        Guid jobId,
        JobState from,
        JobState to,
        string now,
        CancellationToken cancellationToken
    ) =>
        db.ExecuteAsync(
            "INSERT INTO JobStateTransitions (TransitionId, JobId, FromState, ToState, OccurredUtc) VALUES (@transitionId, @jobId, @fromState, @toState, @occurredUtc)",
            cancellationToken,
            HistoryParameters(jobId, from, to, now)
        );

    private static ControlParameter[] HistoryParameters(Guid jobId, JobState from, JobState to, string now) =>
        [
            new("transitionId", Guid.NewGuid().ToString()),
            new("jobId", jobId.ToString()),
            new("fromState", from.ToString()),
            new("toState", to.ToString()),
            new("occurredUtc", now),
        ];

    private static TransferJob ReadJob(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            Enum.Parse<JobState>(reader.GetString(4)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseStamp(reader.GetString(5)),
            ParseStamp(reader.GetString(6)),
            reader.IsDBNull(8) ? null : reader.GetString(8)
        );

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
