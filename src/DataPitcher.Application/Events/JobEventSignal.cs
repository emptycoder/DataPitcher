using System.Collections.Concurrent;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Events;

public sealed class JobEventSignal : IJobEventSignal
{
    private readonly ConcurrentDictionary<Guid, long> _latestEventIds = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<long>> _waiters = [];
    private readonly Action? _afterInitialRead;

    public JobEventSignal() { }

    internal JobEventSignal(Action afterInitialRead) => _afterInitialRead = afterInitialRead;

    public Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken)
    {
        if (_latestEventIds.TryGetValue(jobId, out var latestEventId) && latestEventId > lastObservedEventId)
            return Task.CompletedTask;
        _afterInitialRead?.Invoke();
        var waiter = _waiters.GetOrAdd(jobId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _latestEventIds.TryGetValue(jobId, out latestEventId) && latestEventId > lastObservedEventId
            ? Task.CompletedTask
            : waiter.Task.WaitAsync(cancellationToken);
    }

    public void Publish(JobEvent jobEvent)
    {
        _latestEventIds.AddOrUpdate(
            jobEvent.JobId,
            jobEvent.EventId,
            (_, current) => Math.Max(current, jobEvent.EventId)
        );
        while (true)
        {
            var current = _waiters.GetOrAdd(
                jobEvent.JobId,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            var replacement = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_waiters.TryUpdate(jobEvent.JobId, replacement, current))
            {
                current.TrySetResult(jobEvent.EventId);
                return;
            }
        }
    }
}
