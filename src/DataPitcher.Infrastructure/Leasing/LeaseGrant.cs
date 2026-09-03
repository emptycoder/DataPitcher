namespace DataPitcher.Infrastructure.Leasing;

public sealed record LeaseGrant(
    Guid JobId,
    string OwnerId,
    long FenceToken,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset RenewAfterUtc
);
