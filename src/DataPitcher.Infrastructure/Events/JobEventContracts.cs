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
    private readonly ConcurrentDictionary<Guid, long> _latestEventIds = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<long>> _waiters = [];
    private readonly Action? _afterInitialRead;

    public JobEventSignal() { }
    internal JobEventSignal(Action afterInitialRead) => _afterInitialRead = afterInitialRead;

    public Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken)
    {
        if (_latestEventIds.TryGetValue(jobId, out var latestEventId) && latestEventId > lastObservedEventId) return Task.CompletedTask;
        _afterInitialRead?.Invoke();
        var waiter = _waiters.GetOrAdd(jobId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _latestEventIds.TryGetValue(jobId, out latestEventId) && latestEventId > lastObservedEventId
            ? Task.CompletedTask
            : waiter.Task.WaitAsync(cancellationToken);
    }

    public void Publish(JobEvent jobEvent)
    {
        _latestEventIds.AddOrUpdate(jobEvent.JobId, jobEvent.EventId, (_, current) => Math.Max(current, jobEvent.EventId));
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
