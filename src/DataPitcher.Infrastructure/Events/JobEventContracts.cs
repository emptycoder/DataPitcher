using System.Collections.Concurrent;

namespace DataPitcher.Infrastructure.Events;

public sealed record JobEventPayload(string State, long RowsTransferred, long BytesTransferred);
public sealed record JobEvent(Guid JobId, long EventId, string EventType, JobEventPayload Payload, DateTimeOffset OccurredAtUtc);
public sealed record JobEventAppend(Guid JobId, string EventType, JobEventPayload Payload);
public sealed record JobEventPage(IReadOnlyList<JobEvent> Events, long OldestAvailableEventId);
public interface IJobEventWriter { Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken); }
public interface IJobEventReader { Task<JobEventPage> ReadAfterAsync(Guid jobId, long? lastEventId, CancellationToken cancellationToken); }
public interface IJobEventSignal
{
    Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken);
    void Publish(JobEvent jobEvent);
}

public sealed class EventCursorExpiredException(long oldestAvailableEventId) : InvalidOperationException
{
    public long OldestAvailableEventId { get; } = oldestAvailableEventId;
}

public sealed class JobEventSignal : IJobEventSignal
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<long>> _waiters = [];

    public Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken) =>
        _waiters.GetOrAdd(jobId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);

    public void Publish(JobEvent jobEvent)
    {
        while (true)
        {
            var current = _waiters.GetOrAdd(jobEvent.JobId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            var replacement = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_waiters.TryUpdate(jobEvent.JobId, replacement, current))
            {
                current.TrySetResult(jobEvent.EventId);
                return;
            }
        }
    }
}
