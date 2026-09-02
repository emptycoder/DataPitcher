using System.Globalization;
using LinqToDB.Data;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Leasing;

public sealed class LeaseStore(ControlDatabase database, IClock clock)
{
    public LeaseGrant? Acquire(Guid jobId, string ownerId, TimeSpan ttl)
    {
        var now = clock.UtcNow; var expires = now.Add(ttl); Validate(ownerId, ttl);
        using var db = database.Open();
        var affected = db.Execute("UPDATE JobLeases SET OwnerId = @ownerId, ExpiresUtc = @expiresUtc, FenceToken = FenceToken + 1 WHERE JobId = @jobId AND (OwnerId IS NULL OR ExpiresUtc <= @nowUtc)", Parameters(jobId, ownerId, null, now, expires));
        return affected == 1 ? ReadGrant(db, jobId, ownerId, ttl, now) : null;
    }

    public LeaseGrant? RenewIfDue(LeaseGrant lease, TimeSpan ttl)
    {
        Validate(lease.OwnerId, ttl); var now = clock.UtcNow;
        if (now < lease.RenewAfterUtc) return lease;
        using var db = database.Open(); var expires = now.Add(ttl);
        var affected = db.Execute("UPDATE JobLeases SET ExpiresUtc = @expiresUtc WHERE JobId = @jobId AND OwnerId = @ownerId AND FenceToken = @fenceToken AND ExpiresUtc > @nowUtc", Parameters(lease.JobId, lease.OwnerId, lease.FenceToken, now, expires));
        return affected == 1 ? new LeaseGrant(lease.JobId, lease.OwnerId, lease.FenceToken, expires, RenewAfter(now, ttl)) : null;
    }

    private static DataParameter[] Parameters(Guid jobId, string ownerId, long? fenceToken, DateTimeOffset now, DateTimeOffset expires) => [new("jobId", jobId.ToString()), new("ownerId", ownerId), new("fenceToken", fenceToken), new("nowUtc", Stamp(now)), new("expiresUtc", Stamp(expires))];
    private static LeaseGrant ReadGrant(DataConnection db, Guid jobId, string ownerId, TimeSpan ttl, DateTimeOffset now) { var fenceToken = db.Query<long>("SELECT FenceToken FROM JobLeases WHERE JobId = @jobId", new DataParameter("jobId", jobId.ToString())).Single(); return new(jobId, ownerId, fenceToken, now.Add(ttl), RenewAfter(now, ttl)); }
    private static DateTimeOffset RenewAfter(DateTimeOffset now, TimeSpan ttl) => now.AddTicks(ttl.Ticks * 2 / 3);
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static void Validate(string ownerId, TimeSpan ttl) { if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Lease owner is required.", nameof(ownerId)); if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl)); }
}
