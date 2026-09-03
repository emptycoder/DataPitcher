namespace DataPitcher.Core.Jobs;

public interface ILeaseStore
{
    LeaseGrant? Acquire(Guid jobId, string ownerId, TimeSpan ttl);

    LeaseGrant? RenewIfDue(LeaseGrant lease, TimeSpan ttl);
}
