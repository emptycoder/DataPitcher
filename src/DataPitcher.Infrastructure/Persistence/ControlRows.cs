using LinqToDB.Mapping;

namespace DataPitcher.Infrastructure.Persistence;

[Table("Jobs")] internal sealed class JobRow { [PrimaryKey] public string JobId { get; set; } = ""; public string RunId { get; set; } = ""; public string PlanId { get; set; } = ""; public string IdempotencyKey { get; set; } = ""; public string State { get; set; } = ""; public string CreatedUtc { get; set; } = ""; public string UpdatedUtc { get; set; } = ""; }
[Table("JobStateTransitions")] internal sealed class JobStateTransitionRow { [PrimaryKey] public string TransitionId { get; set; } = ""; public string JobId { get; set; } = ""; public string FromState { get; set; } = ""; public string ToState { get; set; } = ""; public string OccurredUtc { get; set; } = ""; }
[Table("JobLeases")] internal sealed class JobLeaseRow { [PrimaryKey] public string JobId { get; set; } = ""; public string? OwnerId { get; set; } public string? ExpiresUtc { get; set; } public long FenceToken { get; set; } }
