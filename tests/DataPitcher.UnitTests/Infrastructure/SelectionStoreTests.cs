using DataPitcher.Infrastructure.Selections;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class SelectionStoreTests
{
    [Fact]
    public async Task SaveAsync_WhenConnectionAndSnapshotAreSpecified_PersistsThem()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply();
        var store = new SelectionStore(fixture.Database, fixture.Clock); var selectionId = Guid.NewGuid(); var connectionId = Guid.NewGuid(); var snapshotId = Guid.NewGuid();
        _ = await store.SaveAsync(selectionId, "selection", "{}", "\"0\"", CancellationToken.None, connectionId, snapshotId);
        var found = await store.FindAsync(selectionId, CancellationToken.None);
        Assert.NotNull(found);

        Assert.Equal(connectionId, found!.ConnectionId);
        Assert.Equal(snapshotId, found.SnapshotId);
    }
}
