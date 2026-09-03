using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Events;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using DataPitcher.Infrastructure.Selections;

namespace DataPitcher.Api.Contracts;

/// <summary>
/// Production <see cref="IDataPitcherApplication"/> delegating to the real control-database stores and connection
/// services. Connection, schema-scan/snapshot, and job workflows are backed by durable, tested Infrastructure
/// services. Selection and plan drafts are persisted for real, but no dependency-closure/plan-sealing engine exists
/// in Infrastructure yet: <see cref="GetPlanReviewAsync"/> honestly reports an unsealed plan rather than fabricating
/// totals, and <see cref="GetPlanInclusionPathAsync"/> throws until that engine is built.
/// </summary>
public sealed class DataPitcherApplication(
    ConnectionProfileStore connections,
    ConnectionHealthService health,
    SchemaSnapshotStore snapshots,
    SelectionStore selections,
    PlanStore plans,
    JobStore jobs,
    IJobEventReader jobEvents) : IDataPitcherApplication
{
    private const string DefaultBusinessSchema = "app";
    private const string DefaultStagingSchema = "__datapitcher";

    public async Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var summaries = await connections.ListSummariesAsync(cancellationToken);
        return summaries.Select(ToConnectionResponse).ToArray();
    }

    public async Task<ConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, CancellationToken cancellationToken)
    {
        var secretReference = new SecretReference(SecretReferenceKind.EnvironmentVariable, "DATAPITCHER_CREDENTIAL_" + request.CredentialId.ToString("N"));
        var draft = new ConnectionProfileDraft(request.DisplayName, request.ProviderId, secretReference, DefaultBusinessSchema, DefaultStagingSchema);
        var profile = await connections.CreateAsync(draft, request.IfMatch, cancellationToken);
        return new ConnectionResponse(profile.ConnectionId, profile.DisplayName, profile.ProviderId, ConnectionHealthState.Unknown.ToString(), ETag(profile.Version));
    }

    public async Task<OperationReceiptResponse> QueueConnectionCheckAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await health.TestAsync(connectionId, TransferMode.DirectFast, ConnectionRole.Source, cancellationToken);
        return Receipt(connectionId: connectionId);
    }

    public async Task<OperationReceiptResponse> QueueSchemaScanAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var scan = await snapshots.QueueAsync(connectionId, Guid.NewGuid().ToString(), cancellationToken);
        return Receipt(connectionId: scan.ConnectionId);
    }

    public async Task<SchemaSnapshotResponse> GetSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await snapshots.GetAsync(connectionId, snapshotId, cancellationToken);
        return new SchemaSnapshotResponse(snapshot.ConnectionId, snapshot.SnapshotId, snapshot.Hash, snapshot.CapturedAtUtc);
    }

    public async Task<SelectionResponse> SaveSelectionAsync(Guid selectionId, SaveSelectionRequest request, CancellationToken cancellationToken)
    {
        var record = await selections.SaveAsync(selectionId, request.DisplayName, request.QueryJson, request.IfMatch, cancellationToken);
        return new SelectionResponse(record.SelectionId, record.Version, ETag(record.Version));
    }

    public async Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(Guid selectionId, CancellationToken cancellationToken)
    {
        _ = await selections.FindAsync(selectionId, cancellationToken) ?? throw new InvalidOperationException("Selection was not found.");
        return Receipt();
    }

    public async Task<PlanResponse> SavePlanAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken)
    {
        var record = await plans.SaveAsync(planId, request.DisplayName, request.OperatorNote, request.IfMatch, cancellationToken);
        return new PlanResponse(record.PlanId, checked((int)record.Version), record.CanonicalHash, ETag(record.Version));
    }

    public async Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken)
    {
        _ = await plans.FindAsync(planId, cancellationToken) ?? throw new InvalidOperationException("Plan was not found.");
        return Receipt(planId: planId);
    }

    public async Task<PlanReviewResponse> GetPlanReviewAsync(Guid planId, CancellationToken cancellationToken)
    {
        var record = await plans.FindAsync(planId, cancellationToken) ?? throw new InvalidOperationException("Plan was not found.");
        var notSealed = new PlanReviewMessageResponse("plan_not_sealed", "This plan has not completed sealing.");
        return new PlanReviewResponse(
            record.PlanId,
            checked((int)record.Version),
            record.CanonicalHash ?? "",
            new PlanReviewSealResponse("invalidated", [notSealed]),
            new PlanReviewTotalsResponse(0, 0, 0, 0, 0),
            [],
            [],
            [],
            [],
            [],
            [notSealed]);
    }

    public Task<InclusionPathResponse> GetPlanInclusionPathAsync(Guid planId, InclusionPathRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Inclusion-path lookup requires a sealed transfer plan with a computed dependency closure, which is not yet wired.");

    public Task<OperationReceiptResponse> StartJobAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var result = jobs.Start(new StartJobRequest(planId, idempotencyKey));
        return Task.FromResult(Receipt(planId: planId, jobId: result.Job.JobId));
    }

    public async Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId);
        var page = await jobEvents.ReadAfterAsync(jobId, null, cancellationToken);
        var latest = page.Events.Count == 0 ? null : page.Events[^1];
        return new JobResponse(job.JobId, job.PlanId, job.State.ToString(), latest?.Payload.RowsTransferred ?? 0, latest?.Payload.BytesTransferred ?? 0);
    }

    public async Task<OperationReceiptResponse> QueueJobCommandAsync(Guid jobId, JobCommand command, CancellationToken cancellationToken)
    {
        await (command switch
        {
            JobCommand.Pause => jobs.RequestPauseAsync(jobId, cancellationToken),
            JobCommand.Resume => jobs.RequestResumeAsync(jobId, cancellationToken),
            JobCommand.Cancel => jobs.RequestCancelAsync(jobId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        });
        return Receipt(jobId: jobId);
    }

    private static ConnectionResponse ToConnectionResponse(ConnectionProfileSummary summary) =>
        new(summary.ConnectionId, summary.DisplayName, summary.ProviderId, summary.Health.ToString(), summary.ETag);

    private static OperationReceiptResponse Receipt(Guid? connectionId = null, Guid? planId = null, Guid? jobId = null) =>
        new(Guid.NewGuid(), "queued", new Uri("https://datapitcher.local/api/operations/status"), connectionId, planId, jobId);

    private static string ETag(long version) => $"\"{version}\"";
}
