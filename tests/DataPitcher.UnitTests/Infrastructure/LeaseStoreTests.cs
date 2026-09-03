using DataPitcher.Infrastructure.Leasing;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class LeaseStoreTests
{
    [Fact]
    public void LeaseStore_WhenExpiredOwnerIsReplaced_IncrementsTheMonotonicFence()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = store.Acquire(jobId, "worker-a", ttl); Assert.NotNull(first);
        Assert.Null(store.Acquire(jobId, "worker-b", ttl));
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var second = store.Acquire(jobId, "worker-b", ttl); Assert.NotNull(second);
        Assert.Equal(first!.FenceToken + 1, second!.FenceToken);
    }

    [Fact]
    public void LeaseStore_WhenRenewalIsDue_RenewsAtTwoThirdsOfTtlWithoutSleeping()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromSeconds(60);
        var lease = store.Acquire(jobId, "worker-a", ttl)!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(39)); Assert.Equal(lease, store.RenewIfDue(lease, ttl));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); var renewed = store.RenewIfDue(lease, ttl);
        Assert.NotNull(renewed); Assert.Equal(lease.FenceToken, renewed!.FenceToken); Assert.True(renewed.ExpiresUtc > lease.ExpiresUtc);
    }

    [Fact]
    public void LeaseStore_WhenLeaseHasExpired_DoesNotRenewIt()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1); var lease = store.Acquire(jobId, "worker-a", ttl)!;
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));

        Assert.Null(store.RenewIfDue(lease, ttl));
    }

    [Fact]
    public void LeaseStore_WhenOwnerReacquiresAfterExpiry_RefusesToRenewTheStaleFenceEvenWithMatchingOwner()
    {
        using var fixture = new ControlDatabaseFixture(); fixture.Migrator.Apply(); var jobId = fixture.SeedJob();
        var store = new LeaseStore(fixture.Database, fixture.Clock); var ttl = TimeSpan.FromMinutes(1);
        var first = store.Acquire(jobId, "worker-a", ttl)!;
        fixture.Clock.Advance(ttl.Add(TimeSpan.FromTicks(1)));
        var second = store.Acquire(jobId, "worker-a", ttl)!;

        var renewed = store.RenewIfDue(first, ttl);

        Assert.Equal(first.OwnerId, second.OwnerId); Assert.True(second.FenceToken > first.FenceToken); Assert.Null(renewed);
    }

    [Fact]
    public void LeaseStore_WhenOwnerIsBlank_RejectsAcquire()
    {
        using var fixture = new ControlDatabaseFixture(); var store = new LeaseStore(fixture.Database, fixture.Clock);

        Assert.Throws<ArgumentException>(() => store.Acquire(Guid.NewGuid(), "", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void LeaseStore_WhenTtlIsNotPositive_RejectsAcquire()
    {
        using var fixture = new ControlDatabaseFixture(); var store = new LeaseStore(fixture.Database, fixture.Clock);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Acquire(Guid.NewGuid(), "worker-a", TimeSpan.Zero));
    }
}
