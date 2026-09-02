using System.Globalization;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace DataPitcher.Infrastructure.Events;

public sealed class JobEventStore(ControlDatabase database, IClock clock, IJobEventSignal signal) : IJobEventWriter, IJobEventReader
{
    public Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        db.Execute("INSERT OR IGNORE INTO JobEventStreams (JobId, NextEventId, OldestAvailableEventId) VALUES (@job, 1, 1)", new DataParameter("job", append.JobId.ToString()));
        var eventId = db.Query<long>("SELECT NextEventId FROM JobEventStreams WHERE JobId = @job", new DataParameter("job", append.JobId.ToString())).Single();
        var occurredAtUtc = clock.UtcNow;
        db.Execute("UPDATE JobEventStreams SET NextEventId = @next WHERE JobId = @job", new DataParameter("next", eventId + 1), new DataParameter("job", append.JobId.ToString()));
        db.Execute("INSERT INTO JobEvents (JobId, EventId, EventType, State, RowsTransferred, BytesTransferred, OccurredUtc) VALUES (@job, @event, @type, @state, @rows, @bytes, @occurred)", new DataParameter("job", append.JobId.ToString()), new DataParameter("event", eventId), new DataParameter("type", append.EventType), new DataParameter("state", append.Payload.State), new DataParameter("rows", append.Payload.RowsTransferred), new DataParameter("bytes", append.Payload.BytesTransferred), new DataParameter("occurred", occurredAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        transaction.Commit();
        var jobEvent = new JobEvent(append.JobId, eventId, append.EventType, append.Payload, occurredAtUtc);
        signal.Publish(jobEvent);
        return Task.FromResult(jobEvent);
    }

    public async Task<JobEventPage> ReadAfterAsync(Guid jobId, long? lastEventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var stream = await db.GetTable<JobEventStreamRow>().SingleOrDefaultAsync(row => row.JobId == jobId.ToString(), cancellationToken);
        var oldestAvailableEventId = stream?.OldestAvailableEventId ?? 1;
        if (lastEventId is { } suppliedCursor && suppliedCursor < oldestAvailableEventId - 1) throw new EventCursorExpiredException(oldestAvailableEventId);
        var cursor = lastEventId ?? 0;
        var events = await db.GetTable<JobEventRow>().Where(row => row.JobId == jobId.ToString() && row.EventId > cursor).OrderBy(row => row.EventId).ToArrayAsync(cancellationToken);
        return new(events.Select(row => new JobEvent(Guid.Parse(row.JobId), row.EventId, row.EventType, new(row.State, row.RowsTransferred, row.BytesTransferred), DateTimeOffset.Parse(row.OccurredUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))).ToArray(), oldestAvailableEventId);
    }

    public Task TrimBeforeAsync(Guid jobId, long oldestAvailableEventId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(oldestAvailableEventId, 1);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        db.Execute("INSERT OR IGNORE INTO JobEventStreams (JobId, NextEventId, OldestAvailableEventId) VALUES (@job, 1, 1)", new DataParameter("job", jobId.ToString()));
        db.Execute("DELETE FROM JobEvents WHERE JobId = @job AND EventId < @oldest", new DataParameter("job", jobId.ToString()), new DataParameter("oldest", oldestAvailableEventId));
        db.Execute("UPDATE JobEventStreams SET OldestAvailableEventId = @oldest WHERE JobId = @job AND OldestAvailableEventId < @oldest", new DataParameter("oldest", oldestAvailableEventId), new DataParameter("job", jobId.ToString()));
        transaction.Commit();
        return Task.CompletedTask;
    }

    [Table("JobEventStreams")]
    private sealed class JobEventStreamRow
    {
        [Column] public string JobId { get; set; } = "";
        [Column] public long NextEventId { get; set; }
        [Column] public long OldestAvailableEventId { get; set; }
    }

    [Table("JobEvents")]
    private sealed class JobEventRow
    {
        [Column] public string JobId { get; set; } = "";
        [Column] public long EventId { get; set; }
        [Column] public string EventType { get; set; } = "";
        [Column] public string State { get; set; } = "";
        [Column] public long RowsTransferred { get; set; }
        [Column] public long BytesTransferred { get; set; }
        [Column] public string OccurredUtc { get; set; } = "";
    }
}
