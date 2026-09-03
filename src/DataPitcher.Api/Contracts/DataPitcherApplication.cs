using DataPitcher.Application.Connections;
using DataPitcher.Application.Events;
using DataPitcher.Application.Plans;
using DataPitcher.Application.Schema;
using DataPitcher.ControlStore;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Api.Contracts;

public sealed class PlanNotFoundException() : InvalidOperationException("Plan was not found.");

public sealed class PlanNotSealedException() : InvalidOperationException("Plan must be sealed before starting a job.");

/// <summary>
/// Production <see cref="IDataPitcherApplication"/> delegating to the real control-database stores and connection
/// services. Connection, schema-scan/snapshot, selection, plan sealing, and job workflows are backed by durable,
/// tested Infrastructure services. Inclusion-path lookup requires persisted closure provenance.
/// </summary>
public sealed class DataPitcherApplication(
    IConnectionProfileRepository connections,
    ConnectionHealthService health,
    ISchemaSnapshotRepository snapshots,
    ISelectionRepository selections,
    IPlanRepository plans,
    IJobRepository jobs,
    IJobEventReader jobEvents,
    PlanSealingService? sealing = null,
    ISecretWriter? secretWriter = null
) : IDataPitcherApplication
{
    private const string DefaultBusinessSchema = "app";
    private const string DefaultStagingSchema = "__datapitcher";

    public async Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var summaries = await connections.ListSummariesAsync(cancellationToken);
        return summaries.Select(ToConnectionResponse).ToArray();
    }

    public async Task<ConnectionResponse> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            throw new ArgumentException("A connection string is required.", nameof(request));
        var secretReference = await (
            secretWriter ?? throw new InvalidOperationException("Connection secret storage is not configured.")
        ).StoreAsync(request.CredentialId, request.ConnectionString, cancellationToken);
        var draft = new ConnectionProfileDraft(
            request.DisplayName,
            request.ProviderId,
            secretReference,
            DefaultBusinessSchema,
            DefaultStagingSchema
        );
        var profile = await connections.CreateAsync(draft, request.IfMatch, cancellationToken);
        return new ConnectionResponse(
            profile.ConnectionId,
            profile.DisplayName,
            profile.ProviderId,
            ConnectionHealthState.Unknown.ToString(),
            ETag(profile.Version)
        );
    }

    public async Task<ConnectionResponse> UpdateConnectionAsync(
        Guid connectionId,
        UpdateConnectionRequest request,
        CancellationToken cancellationToken
    )
    {
        var existing = await connections.GetProfileAsync(connectionId, cancellationToken);
        var replacingSecret = !string.IsNullOrWhiteSpace(request.ConnectionString);
        var secretReference = replacingSecret
            ? await (
                secretWriter ?? throw new InvalidOperationException("Connection secret storage is not configured.")
            ).StoreAsync(Guid.NewGuid(), request.ConnectionString!, cancellationToken)
            : existing.SecretReference;
        var profile = await connections.UpdateAsync(
            connectionId,
            new ConnectionProfileDraft(
                request.DisplayName,
                request.ProviderId,
                secretReference,
                existing.BusinessSchema,
                existing.StagingSchema
            ),
            request.IfMatch,
            cancellationToken
        );
        if (replacingSecret && secretWriter is not null)
            await secretWriter.RemoveAsync(existing.SecretReference, cancellationToken);
        var summary = await connections.GetSummaryAsync(profile.ConnectionId, cancellationToken);
        return ToConnectionResponse(summary);
    }

    public async Task DeleteConnectionAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken)
    {
        var profile = await connections.GetProfileAsync(connectionId, cancellationToken);
        await connections.DeleteAsync(connectionId, ifMatch, cancellationToken);
        if (secretWriter is not null)
            await secretWriter.RemoveAsync(profile.SecretReference, cancellationToken);
    }

    public async Task<OperationReceiptResponse> QueueConnectionCheckAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        await health.TestAsync(connectionId, TransferMode.DirectFast, ConnectionRole.Source, cancellationToken);
        return Receipt(connectionId: connectionId);
    }

    public async Task<OperationReceiptResponse> QueueSchemaScanAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        var scan = await snapshots.QueueAsync(connectionId, Guid.NewGuid().ToString(), cancellationToken);
        return Receipt(scan.ScanId, connectionId: scan.ConnectionId, state: "queued");
    }

    public async Task<OperationStatusResponse?> GetOperationStatusAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        var scan = await snapshots.FindScanAsync(operationId, cancellationToken);
        if (scan is not null)
            return new(
                scan.ScanId,
                "schema-scan",
                scan.State.ToString(),
                scan.State is SchemaScanState.Completed or SchemaScanState.Failed,
                scan.State is SchemaScanState.Failed,
                scan.FailureCode,
                scan.ConnectionId,
                scan.SnapshotId,
                null,
                null
            );

        var job = jobs.Find(operationId);
        return job is null
            ? null
            : new(
                job.JobId,
                "job",
                job.State.ToString(),
                job.State is JobState.Cancelled or JobState.Succeeded or JobState.Failed or JobState.VerificationFailed,
                job.State is JobState.Failed or JobState.VerificationFailed,
                job.FailureCode,
                null,
                null,
                job.PlanId,
                job.JobId
            );
    }

    public async Task<IReadOnlyList<SchemaSnapshotSummaryResponse>> ListSnapshotsAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    ) =>
        (await snapshots.ListAsync(connectionId, cancellationToken))
            .Select(snapshot => new SchemaSnapshotSummaryResponse(
                snapshot.SnapshotId,
                snapshot.Hash,
                snapshot.CapturedAtUtc
            ))
            .ToArray();

    public async Task<SchemaSnapshotResponse> GetSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots.GetAsync(connectionId, snapshotId, cancellationToken);
        return ToSnapshotResponse(snapshot);
    }

    public async Task<SchemaSnapshotResponse?> FindSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots.FindAsync(connectionId, snapshotId, cancellationToken);
        return snapshot is null ? null : ToSnapshotResponse(snapshot);
    }

    public async Task<SelectionResponse> SaveSelectionAsync(
        Guid selectionId,
        SaveSelectionRequest request,
        CancellationToken cancellationToken
    )
    {
        var record = await selections.SaveAsync(
            selectionId,
            request.DisplayName,
            request.QueryJson,
            request.IfMatch,
            cancellationToken
        );
        return new SelectionResponse(record.SelectionId, record.Version, ETag(record.Version));
    }

    public Task DeleteSelectionAsync(Guid selectionId, string ifMatch, CancellationToken cancellationToken) =>
        selections.DeleteAsync(selectionId, ifMatch, cancellationToken);

    public async Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(
        Guid selectionId,
        CancellationToken cancellationToken
    )
    {
        _ =
            await selections.FindAsync(selectionId, cancellationToken)
            ?? throw new InvalidOperationException("Selection was not found.");
        return Receipt();
    }

    public async Task<PlanResponse> SavePlanAsync(
        Guid planId,
        SavePlanRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.SelectionId is Guid selectionId
            && await selections.FindAsync(selectionId, cancellationToken) is null
        )
            throw new ArgumentException("Selection was not found.", nameof(request));
        if (request.SourceConnectionId is Guid sourceConnectionId)
            _ = await connections.GetSummaryAsync(sourceConnectionId, cancellationToken);
        if (request.TargetConnectionId is Guid targetConnectionId)
            _ = await connections.GetSummaryAsync(targetConnectionId, cancellationToken);
        var record = await plans.SaveAsync(
            planId,
            request.DisplayName,
            request.OperatorNote,
            request.IfMatch,
            cancellationToken,
            request.SelectionId,
            request.SourceConnectionId,
            request.TargetConnectionId
        );
        return new PlanResponse(
            record.PlanId,
            checked((int)record.Version),
            record.CanonicalHash,
            ETag(record.Version)
        );
    }

    public async Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan =
            await plans.FindAsync(planId, cancellationToken)
            ?? throw new InvalidOperationException("Plan was not found.");
        if (plan.SelectionId is not null && plan.SourceConnectionId is not null && plan.TargetConnectionId is not null)
            await (sealing ?? throw new InvalidOperationException("Plan sealing is not configured.")).SealAsync(
                planId,
                cancellationToken
            );
        return Receipt(planId: planId);
    }

    public async Task<PlanReviewResponse> GetPlanReviewAsync(Guid planId, CancellationToken cancellationToken)
    {
        var record = await plans.FindAsync(planId, cancellationToken) ?? throw new PlanNotFoundException();
        var selection = record.SelectionId is Guid selectionId
            ? await selections.FindAsync(selectionId, cancellationToken)
                ?? throw new ArgumentException("Selection was not found.")
            : null;
        var source = record.SourceConnectionId is Guid sourceConnectionId
            ? ToConnectionResponse(await connections.GetSummaryAsync(sourceConnectionId, cancellationToken))
            : null;
        var target = record.TargetConnectionId is Guid targetConnectionId
            ? ToConnectionResponse(await connections.GetSummaryAsync(targetConnectionId, cancellationToken))
            : null;
        var content = await plans.LoadContentAsync(planId, cancellationToken);
        if (content is not null)
            return new PlanReviewResponse(
                record.PlanId,
                checked((int)record.Version),
                record.CanonicalHash ?? "",
                new PlanReviewSealResponse("sealed", []),
                new PlanReviewTotalsResponse(
                    content.ManifestTotals.Included,
                    content.ManifestTotals.PlannedWrites,
                    content.ManifestTotals.Inserts,
                    content.ManifestTotals.Updates,
                    0
                ),
                [],
                content
                    .Tables.Select(
                        (table, index) =>
                            new PlanReviewTableResponse(
                                new PlanReviewAddressResponse(table.Mapping.Source.Schema, table.Mapping.Source.Name),
                                new PlanReviewAddressResponse(table.Mapping.Target.Schema, table.Mapping.Target.Name),
                                table.State.ToString(),
                                index,
                                table.Manifest.Included,
                                table.Manifest.PlannedWrites,
                                table.Manifest.Inserts,
                                table.Manifest.Updates,
                                0,
                                table
                                    .Mapping.Columns.Select(column => new PlanReviewColumnResponse(
                                        column.Source,
                                        column.Target
                                    ))
                                    .ToArray()
                            )
                    )
                    .ToArray(),
                content
                    .ConflictPolicies.Select(policy => new PlanReviewConflictResponse(
                        policy.Table.Schema + "." + policy.Table.Name,
                        policy.Policy.ToString(),
                        ""
                    ))
                    .ToArray(),
                [],
                [],
                [],
                selection is null
                    ? null
                    : new PlanReviewSelectionResponse(
                        selection.SelectionId,
                        selection.DisplayName,
                        selection.ConnectionId,
                        selection.SnapshotId
                    ),
                source,
                target
            );
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
            [notSealed],
            selection is null
                ? null
                : new PlanReviewSelectionResponse(
                    selection.SelectionId,
                    selection.DisplayName,
                    selection.ConnectionId,
                    selection.SnapshotId
                ),
            source,
            target
        );
    }

    public Task<InclusionPathResponse> GetPlanInclusionPathAsync(
        Guid planId,
        InclusionPathRequest request,
        CancellationToken cancellationToken
    ) =>
        throw new InvalidOperationException(
            "Inclusion-path lookup requires a sealed transfer plan with a computed dependency closure, which is not yet wired."
        );

    public async Task<OperationReceiptResponse> StartJobAsync(
        Guid planId,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        _ = await plans.FindAsync(planId, cancellationToken) ?? throw new PlanNotFoundException();
        if (await plans.LoadContentAsync(planId, cancellationToken) is null)
            throw new PlanNotSealedException();
        var result = jobs.Start(new StartJobRequest(planId, idempotencyKey));
        return Receipt(result.Job.JobId, planId: planId, jobId: result.Job.JobId, state: "queued");
    }

    public async Task<IReadOnlyList<JobSummaryResponse>> ListJobsAsync(CancellationToken cancellationToken)
    {
        var jobsToList = jobs.List(cancellationToken);
        return await Task.WhenAll(
            jobsToList.Select(async job =>
            {
                var current = await GetJobAsync(job.JobId, cancellationToken);
                return new JobSummaryResponse(
                    job.JobId,
                    job.PlanId,
                    job.State.ToString(),
                    job.CreatedUtc,
                    job.UpdatedUtc,
                    current.RowsTransferred,
                    current.BytesTransferred
                );
            })
        );
    }

    public async Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = jobs.Get(jobId);
        var page = await jobEvents.ReadAfterAsync(jobId, null, cancellationToken);
        var latest = page.Events.Count == 0 ? null : page.Events[^1];
        return new JobResponse(
            job.JobId,
            job.PlanId,
            job.State.ToString(),
            latest?.Payload.RowsTransferred ?? 0,
            latest?.Payload.BytesTransferred ?? 0
        );
    }

    public async Task<OperationReceiptResponse> QueueJobCommandAsync(
        Guid jobId,
        JobCommand command,
        CancellationToken cancellationToken
    )
    {
        await (
            command switch
            {
                JobCommand.Pause => jobs.RequestPauseAsync(jobId, cancellationToken),
                JobCommand.Resume => jobs.RequestResumeAsync(jobId, cancellationToken),
                JobCommand.Cancel => jobs.RequestCancelAsync(jobId, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            }
        );
        return Receipt(jobId, jobId: jobId);
    }

    private static ConnectionResponse ToConnectionResponse(ConnectionProfileSummary summary) =>
        new(summary.ConnectionId, summary.DisplayName, summary.ProviderId, summary.Health.ToString(), summary.ETag);

    private static SchemaSnapshotResponse ToSnapshotResponse(StoredSchemaSnapshot snapshot) =>
        new(snapshot.ConnectionId, snapshot.SnapshotId, snapshot.Hash, snapshot.CapturedAtUtc)
        {
            Tables = snapshot
                .Content.Tables.OrderBy(table => table.Schema, StringComparer.Ordinal)
                .ThenBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => new SchemaSnapshotTableResponse(
                    table.Schema,
                    table.Name,
                    table
                        .Columns.Select(column => new SchemaSnapshotColumnResponse(
                            column.Name,
                            column.StoreType,
                            column.IsNullable
                        ))
                        .ToArray(),
                    table.PrimaryKey is null
                        ? null
                        : new SchemaSnapshotKeyResponse(table.PrimaryKey.Name, table.PrimaryKey.Columns.ToArray())
                ))
                .ToArray(),
            ForeignKeys = snapshot
                .Content.ForeignKeys.OrderBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
                .ThenBy(foreignKey => foreignKey.ChildTable.Schema, StringComparer.Ordinal)
                .ThenBy(foreignKey => foreignKey.ChildTable.Name, StringComparer.Ordinal)
                .Select(foreignKey => new SchemaSnapshotForeignKeyResponse(
                    foreignKey.Name,
                    new SchemaSnapshotAddressResponse(foreignKey.ChildTable.Schema, foreignKey.ChildTable.Name),
                    new SchemaSnapshotAddressResponse(foreignKey.ParentTable.Schema, foreignKey.ParentTable.Name),
                    foreignKey.ChildColumns.ToArray(),
                    foreignKey.ParentColumns.ToArray(),
                    foreignKey.IsEnforced,
                    foreignKey.IsTrusted
                ))
                .ToArray(),
        };

    private static OperationReceiptResponse Receipt(
        Guid? operationId = null,
        Guid? connectionId = null,
        Guid? planId = null,
        Guid? jobId = null,
        string state = "unknown"
    )
    {
        var id = operationId ?? Guid.NewGuid();
        return new(id, state, new Uri("https://datapitcher.local/api/operations/" + id), connectionId, planId, jobId);
    }

    private static string ETag(long version) => $"\"{version}\"";
}
