using System.Globalization;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Time;

namespace DataPitcher.ControlStore;

public sealed class JobEventStore(ControlDatabase database, IClock clock, IJobEventSignal signal)
    : IJobEventWriter,
        IJobEventReader
{
    private const string EnsureStreamSql =
        "INSERT OR IGNORE INTO JobEventStreams (JobId, NextEventId, OldestAvailableEventId) VALUES (@job, 1, 1)";

    private const string NextEventIdSql = "SELECT NextEventId FROM JobEventStreams WHERE JobId = @job";

    public Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        db.Execute(EnsureStreamSql, new ControlParameter("job", append.JobId.ToString()));
        var eventId = db.Query<long>(NextEventIdSql, new ControlParameter("job", append.JobId.ToString())).Single();
        var occurredAtUtc = clock.UtcNow;
        db.Execute(
            "UPDATE JobEventStreams SET NextEventId = @next WHERE JobId = @job",
            new ControlParameter("next", eventId + 1),
            new ControlParameter("job", append.JobId.ToString())
        );
        db.Execute(
            "INSERT INTO JobEvents (JobId, EventId, EventType, State, RowsTransferred, BytesTransferred, OccurredUtc) VALUES (@job, @event, @type, @state, @rows, @bytes, @occurred)",
            new ControlParameter("job", append.JobId.ToString()),
            new ControlParameter("event", eventId),
            new ControlParameter("type", append.EventType),
            new ControlParameter("state", append.Payload.State),
            new ControlParameter("rows", append.Payload.RowsTransferred),
            new ControlParameter("bytes", append.Payload.BytesTransferred),
            new ControlParameter("occurred", occurredAtUtc.ToString("O", CultureInfo.InvariantCulture))
        );
        transaction.Commit();
        var jobEvent = new JobEvent(append.JobId, eventId, append.EventType, append.Payload, occurredAtUtc);
        signal.Publish(jobEvent);
        return Task.FromResult(jobEvent);
    }

    public async Task<JobEventPage> ReadAfterAsync(Guid jobId, long? lastEventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var streams = await db.QueryAsync(
            "SELECT OldestAvailableEventId FROM JobEventStreams WHERE JobId = @job",
            reader => reader.GetInt64(0),
            cancellationToken,
            new ControlParameter("job", jobId.ToString())
        );
        var oldestAvailableEventId = streams.Count == 0 ? 1 : streams.Single();
        if (lastEventId is { } suppliedCursor && suppliedCursor < oldestAvailableEventId - 1)
            throw new EventCursorExpiredException(oldestAvailableEventId);
        var cursor = lastEventId ?? 0;
        var events = await db.QueryAsync(
            "SELECT JobId, EventId, EventType, State, RowsTransferred, BytesTransferred, OccurredUtc FROM JobEvents WHERE JobId = @job AND EventId > @cursor ORDER BY EventId",
            reader => new JobEvent(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetString(2),
                new(reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5)),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            ),
            cancellationToken,
            new ControlParameter("job", jobId.ToString()),
            new ControlParameter("cursor", cursor)
        );
        return new(events, oldestAvailableEventId);
    }

    public Task TrimBeforeAsync(Guid jobId, long oldestAvailableEventId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(oldestAvailableEventId, 1);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        db.Execute(EnsureStreamSql, new ControlParameter("job", jobId.ToString()));
        var nextEventId = db.Query<long>(NextEventIdSql, new ControlParameter("job", jobId.ToString())).Single();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(oldestAvailableEventId, nextEventId);
        db.Execute(
            "DELETE FROM JobEvents WHERE JobId = @job AND EventId < @oldest",
            new ControlParameter("job", jobId.ToString()),
            new ControlParameter("oldest", oldestAvailableEventId)
        );
        db.Execute(
            "UPDATE JobEventStreams SET OldestAvailableEventId = @oldest WHERE JobId = @job AND OldestAvailableEventId < @oldest",
            new ControlParameter("oldest", oldestAvailableEventId),
            new ControlParameter("job", jobId.ToString())
        );
        transaction.Commit();
        return Task.CompletedTask;
    }
}
