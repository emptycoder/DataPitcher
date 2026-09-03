namespace DataPitcher.Core.Jobs;

public sealed record JobEventPayload(string State, long RowsTransferred, long BytesTransferred);

public sealed record JobEvent(
    Guid JobId,
    long EventId,
    string EventType,
    JobEventPayload Payload,
    DateTimeOffset OccurredAtUtc
);

public sealed record JobEventAppend(Guid JobId, string EventType, JobEventPayload Payload);

public sealed record JobEventPage(IReadOnlyList<JobEvent> Events, long OldestAvailableEventId);

public interface IJobEventWriter
{
    Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken);
}

public interface IJobEventReader
{
    Task<JobEventPage> ReadAfterAsync(Guid jobId, long? lastEventId, CancellationToken cancellationToken);
}

public interface IJobEventSignal
{
    Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken);
    void Publish(JobEvent jobEvent);
}

public sealed class EventCursorExpiredException(long oldestAvailableEventId) : InvalidOperationException
{
    public long OldestAvailableEventId { get; } = oldestAvailableEventId;
}
