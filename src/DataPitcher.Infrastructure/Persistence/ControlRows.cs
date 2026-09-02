using LinqToDB.Mapping;

namespace DataPitcher.Infrastructure.Persistence;

[Table("Jobs")] internal sealed class JobRow { [PrimaryKey, Column("JobId")] public string JobId { get; set; } = ""; [Column("RunId")] public string RunId { get; set; } = ""; [Column("PlanId")] public string PlanId { get; set; } = ""; [Column("IdempotencyKey")] public string IdempotencyKey { get; set; } = ""; [Column("State")] public string State { get; set; } = ""; [Column("CreatedUtc")] public string CreatedUtc { get; set; } = ""; [Column("UpdatedUtc")] public string UpdatedUtc { get; set; } = ""; }
[Table("JobStateTransitions")] internal sealed class JobStateTransitionRow { [PrimaryKey, Column("TransitionId")] public string TransitionId { get; set; } = ""; [Column("JobId")] public string JobId { get; set; } = ""; [Column("FromState")] public string FromState { get; set; } = ""; [Column("ToState")] public string ToState { get; set; } = ""; [Column("OccurredUtc")] public string OccurredUtc { get; set; } = ""; }
[Table("JobLeases")] internal sealed class JobLeaseRow { [PrimaryKey, Column("JobId")] public string JobId { get; set; } = ""; [Column("OwnerId")] public string? OwnerId { get; set; } [Column("ExpiresUtc")] public string? ExpiresUtc { get; set; } [Column("FenceToken")] public long FenceToken { get; set; } }
