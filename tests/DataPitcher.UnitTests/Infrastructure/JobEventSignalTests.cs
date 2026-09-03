using DataPitcher.Application.Events;
using DataPitcher.ControlStore;
using DataPitcher.Core.Jobs;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class JobEventSignalTests
{
    [Fact]
    public async Task JobEventSignal_WhenAnEventCommitsBeforeWaiterRegistration_DoesNotMissTheNotification()
    {
        using var fixture = new ControlDatabaseFixture();
        fixture.Migrator.Apply();
        using var beforeRegistration = new Barrier(2);
        using var afterPublication = new Barrier(2);
        var signal = new JobEventSignal(() =>
        {
            beforeRegistration.SignalAndWait();
            afterPublication.SignalAndWait();
        });
        var events = new JobEventStore(fixture.Database, fixture.Clock, signal);
        var jobId = Guid.NewGuid();
        var wait = Task.Run(() => signal.WaitAsync(jobId, 0, CancellationToken.None));

        beforeRegistration.SignalAndWait();
        var appended = await events.AppendAsync(
            new JobEventAppend(jobId, "progress", new JobEventPayload("running", 10, 100)),
            CancellationToken.None
        );
        afterPublication.SignalAndWait();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([appended], (await events.ReadAfterAsync(jobId, 0, CancellationToken.None)).Events);
    }
}
