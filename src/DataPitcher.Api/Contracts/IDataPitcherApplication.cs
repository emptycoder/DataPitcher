using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Api.Contracts;

public interface IDataPitcherApplication
{
    Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken);
    Task<ConnectionResponse> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken
    );
    Task<ConnectionResponse> UpdateConnectionAsync(
        Guid connectionId,
        UpdateConnectionRequest request,
        CancellationToken cancellationToken
    );
    Task DeleteConnectionAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken);
    Task<ConnectionDetailsResponse> GetConnectionDetailsAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<ConnectionTestResponse> TestConnectionAsync(
        ConnectionTestRequest request,
        CancellationToken cancellationToken
    );
    Task<OperationReceiptResponse> QueueConnectionCheckAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueSchemaScanAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SchemaSnapshotSummaryResponse>> ListSnapshotsAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    );
    Task DeleteSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);
    Task<OperationStatusResponse?> GetOperationStatusAsync(Guid operationId, CancellationToken cancellationToken);
    Task<SchemaSnapshotResponse> GetSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    );
    Task<SchemaSnapshotResponse?> FindSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        CancellationToken cancellationToken
    );
    Task<SelectionResponse> SaveSelectionAsync(
        Guid selectionId,
        SaveSelectionRequest request,
        CancellationToken cancellationToken
    );
    Task DeleteSelectionAsync(Guid selectionId, string ifMatch, CancellationToken cancellationToken);
    Task<SelectionDetailsResponse> GetSelectionDetailsAsync(Guid selectionId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(Guid selectionId, CancellationToken cancellationToken);
    Task<PlanResponse> SavePlanAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken);
    Task<PlanDetailsResponse> GetPlanDetailsAsync(Guid planId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken);
    Task<PlanReviewResponse> GetPlanReviewAsync(Guid planId, CancellationToken cancellationToken);
    Task<InclusionPathResponse> GetPlanInclusionPathAsync(
        Guid planId,
        InclusionPathRequest request,
        CancellationToken cancellationToken
    );
    Task<OperationReceiptResponse> StartJobAsync(
        Guid planId,
        string idempotencyKey,
        CancellationToken cancellationToken
    );
    Task<IReadOnlyList<JobSummaryResponse>> ListJobsAsync(CancellationToken cancellationToken);
    Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueJobCommandAsync(
        Guid jobId,
        JobCommand command,
        CancellationToken cancellationToken
    );
}
