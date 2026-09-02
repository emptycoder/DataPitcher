using DataPitcher.Api.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DataPitcher.Api.Endpoints;

public sealed record JobCommandRequest(JobCommand Command);

public static class EndpointGroups
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/connections");
        connections.MapGet("", ListConnectionsAsync);
        connections.MapPost("", CreateConnectionAsync);
        connections.MapPost("/{connectionId:guid}/checks", QueueConnectionCheckAsync);
        connections.MapPost("/{connectionId:guid}/schema-scans", QueueSchemaScanAsync);
        connections.MapGet("/{connectionId:guid}/snapshots/{snapshotId:guid}", GetSnapshotAsync);

        var selections = app.MapGroup("/api/selections");
        selections.MapPut("/{selectionId:guid}", SaveSelectionAsync);
        selections.MapPost("/{selectionId:guid}/evaluations", QueueSelectionEvaluationAsync);

        var plans = app.MapGroup("/api/plans");
        plans.MapPut("/{planId:guid}", SavePlanAsync);
        plans.MapPost("/{planId:guid}/seal", QueuePlanSealAsync);
        plans.MapPost("/{planId:guid}/jobs", StartJobAsync);

        var jobs = app.MapGroup("/api/jobs");
        jobs.MapGet("/{jobId:guid}", GetJobAsync);
        jobs.MapPost("/{jobId:guid}/commands", QueueJobCommandAsync);
    }

    private static async Task<Ok<IReadOnlyList<ConnectionResponse>>> ListConnectionsAsync(
        IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.ListConnectionsAsync(cancellationToken));

    private static async Task<Ok<ConnectionResponse>> CreateConnectionAsync(
        CreateConnectionRequest request, IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.CreateConnectionAsync(request, cancellationToken));

    private static async Task<Accepted<OperationReceiptResponse>> QueueConnectionCheckAsync(
        Guid connectionId, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        var receipt = await application.QueueConnectionCheckAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Accepted<OperationReceiptResponse>> QueueSchemaScanAsync(
        Guid connectionId, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        var receipt = await application.QueueSchemaScanAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Ok<SchemaSnapshotResponse>> GetSnapshotAsync(
        Guid connectionId, Guid snapshotId, IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.GetSnapshotAsync(connectionId, snapshotId, cancellationToken));

    private static async Task<Results<Ok<SelectionResponse>, ProblemHttpResult>> SaveSelectionAsync(
        Guid selectionId, SaveSelectionRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        return TypedResults.Ok(await application.SaveSelectionAsync(selectionId, request, cancellationToken));
    }

    private static async Task<Accepted<OperationReceiptResponse>> QueueSelectionEvaluationAsync(
        Guid selectionId, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        var receipt = await application.QueueSelectionEvaluationAsync(selectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<PlanResponse>, ProblemHttpResult>> SavePlanAsync(
        Guid planId, SavePlanRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        return TypedResults.Ok(await application.SavePlanAsync(planId, request, cancellationToken));
    }

    private static async Task<Accepted<OperationReceiptResponse>> QueuePlanSealAsync(
        Guid planId, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        var receipt = await application.QueuePlanSealAsync(planId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> StartJobAsync(
        Guid planId, HttpRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency key is required.");

        var receipt = await application.StartJobAsync(planId, values.ToString(), cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Ok<JobResponse>> GetJobAsync(
        Guid jobId, IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.GetJobAsync(jobId, cancellationToken));

    private static async Task<Accepted<OperationReceiptResponse>> QueueJobCommandAsync(
        Guid jobId, JobCommandRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        var receipt = await application.QueueJobCommandAsync(jobId, request.Command, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }
}
