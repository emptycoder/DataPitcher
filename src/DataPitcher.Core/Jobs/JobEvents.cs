using System.Text.Json.Serialization;

namespace DataPitcher.Core.Jobs;

/// <summary>Progress or state change of a job. <paramref name="Detail"/> carries the reason for a failure.</summary>
/// <summary>Progress or state change of a job. <paramref name="Detail"/> carries the reason for a failure.</summary>
public sealed record JobEventPayload(
    string State,
    long RowsTransferred,
    long BytesTransferred,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null
);

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
