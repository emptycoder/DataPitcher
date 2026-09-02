using System.Globalization;
using LinqToDB;
using LinqToDB.Data;
using DataPitcher.Core.Jobs;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Persistence;

public sealed record TransferJob(Guid JobId, Guid RunId, Guid PlanId, string IdempotencyKey, JobState State);
public sealed record StartJobRequest(Guid PlanId, string IdempotencyKey);
public sealed record StartJobResult(TransferJob Job, bool Created);
public sealed record JobTransitionResult(TransferJob? Job, int RowsAffected);

public sealed class JobStore(ControlDatabase database, IClock clock)
{
    public StartJobResult Start(StartJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(request));
        using var db = database.Open(); using var transaction = db.BeginTransaction();
        var existing = db.GetTable<JobRow>().SingleOrDefault(row => row.IdempotencyKey == request.IdempotencyKey);
        if (existing is not null) return new(ToJob(existing), false);
        var now = Stamp(clock.UtcNow); var row = new JobRow { JobId = Guid.NewGuid().ToString(), RunId = Guid.NewGuid().ToString(), PlanId = request.PlanId.ToString(), IdempotencyKey = request.IdempotencyKey, State = JobState.Draft.ToString(), CreatedUtc = now, UpdatedUtc = now };
        db.Insert(row); PersistTransition(db, row, JobState.Draft, JobState.Queued, now); db.Insert(new JobLeaseRow { JobId = row.JobId, FenceToken = 0 }); transaction.Commit();
        return new(ToJob(row), true);
    }

    public JobTransitionResult TryTransition(LeaseGrant lease, JobState to)
    {
        using var db = database.Open(); using var transaction = db.BeginTransaction(); var row = db.GetTable<JobRow>().Single(row => row.JobId == lease.JobId.ToString()); var from = Enum.Parse<JobState>(row.State); JobStateMachine.EnsureTransition(from, to); var now = Stamp(clock.UtcNow);
        var affected = db.Execute("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState AND EXISTS (SELECT 1 FROM JobLeases WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc)", new DataParameter("toState", to.ToString()), new DataParameter("nowUtc", now), new DataParameter("jobId", lease.JobId.ToString()), new DataParameter("fromState", from.ToString()), new DataParameter("ownerId", lease.OwnerId), new DataParameter("fenceToken", lease.FenceToken));
        if (affected == 0) return new(null, 0);
        PersistHistory(db, lease.JobId, from, to, now); row.State = to.ToString(); row.UpdatedUtc = now; transaction.Commit(); return new(ToJob(row), 1);
    }

    public TransferJob Get(Guid jobId) => ToJob(database.Open().GetTable<JobRow>().Single(row => row.JobId == jobId.ToString()));
    public IReadOnlyList<(JobState From, JobState To)> GetHistory(Guid jobId) => database.Open().GetTable<JobStateTransitionRow>().Where(row => row.JobId == jobId.ToString()).OrderBy(row => row.OccurredUtc).Select(row => new { row.FromState, row.ToState }).AsEnumerable().Select(row => (Enum.Parse<JobState>(row.FromState), Enum.Parse<JobState>(row.ToState))).ToArray();
    private static void PersistTransition(DataConnection db, JobRow row, JobState from, JobState to, string now) { db.Execute("UPDATE Jobs SET State = @toState, UpdatedUtc = @nowUtc WHERE JobId = @jobId AND State = @fromState", new DataParameter("toState", to.ToString()), new DataParameter("nowUtc", now), new DataParameter("jobId", row.JobId), new DataParameter("fromState", from.ToString())); row.State = to.ToString(); row.UpdatedUtc = now; PersistHistory(db, Guid.Parse(row.JobId), from, to, now); }
    private static void PersistHistory(DataConnection db, Guid jobId, JobState from, JobState to, string now) => db.Insert(new JobStateTransitionRow { TransitionId = Guid.NewGuid().ToString(), JobId = jobId.ToString(), FromState = from.ToString(), ToState = to.ToString(), OccurredUtc = now });
    private static TransferJob ToJob(JobRow row) => new(Guid.Parse(row.JobId), Guid.Parse(row.RunId), Guid.Parse(row.PlanId), row.IdempotencyKey, Enum.Parse<JobState>(row.State));
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
