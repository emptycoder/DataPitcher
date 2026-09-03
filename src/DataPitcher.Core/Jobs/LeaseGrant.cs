namespace DataPitcher.Core.Jobs;

public sealed record LeaseGrant(
    Guid JobId,
    string OwnerId,
    long FenceToken,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset RenewAfterUtc
);
