using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DataPitcher.Api.Endpoints;

public sealed record JobCommandRequest(JobCommand Command);

public static class EndpointGroups
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/connections");
        connections.MapGet("", ListConnectionsAsync).RequireAuthorization(ApiPolicyNames.ConnectionsRead);
        connections.MapPost("", CreateConnectionAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite);
        connections.MapPost("/{connectionId:guid}/checks", QueueConnectionCheckAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite);
        connections.MapPost("/{connectionId:guid}/schema-scans", QueueSchemaScanAsync).RequireAuthorization(ApiPolicyNames.SchemaWrite);
        connections.MapGet("/{connectionId:guid}/snapshots/{snapshotId:guid}", GetSnapshotAsync).RequireAuthorization(ApiPolicyNames.SchemaRead);

        var selections = app.MapGroup("/api/selections");
        selections.MapPut("/{selectionId:guid}", SaveSelectionAsync).RequireAuthorization(ApiPolicyNames.SelectionsWrite);
        selections.MapPost("/{selectionId:guid}/evaluations", QueueSelectionEvaluationAsync).RequireAuthorization(ApiPolicyNames.SelectionsWrite);

        var plans = app.MapGroup("/api/plans");
        plans.MapPut("/{planId:guid}", SavePlanAsync).RequireAuthorization(ApiPolicyNames.PlansWrite);
        plans.MapPost("/{planId:guid}/seal", QueuePlanSealAsync).RequireAuthorization(ApiPolicyNames.PlansSeal);
        plans.MapPost("/{planId:guid}/jobs", StartJobAsync).RequireAuthorization(ApiPolicyNames.TransfersStart);

        var jobs = app.MapGroup("/api/jobs");
        jobs.MapGet("/{jobId:guid}", GetJobAsync).RequireAuthorization(ApiPolicyNames.TransfersRead);
        jobs.MapPost("/{jobId:guid}/commands", QueueJobCommandAsync).RequireAuthorization(ApiPolicyNames.TransfersWrite);
    }

    private static async Task<ProblemHttpResult?> AuthorizeResourceAsync(
        IAuthorizationService authorizationService, ClaimsPrincipal user, ApiResource resource, Permission permission)
    {
        var result = await authorizationService.AuthorizeAsync(user, resource, new ResourcePermissionRequirement(permission));
        return result.Succeeded ? null : ApiAuthorizationResults.Forbidden();
    }

    private static async Task<Ok<IReadOnlyList<ConnectionResponse>>> ListConnectionsAsync(
        IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.ListConnectionsAsync(cancellationToken));

    private static async Task<Ok<ConnectionResponse>> CreateConnectionAsync(
        CreateConnectionRequest request, IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.CreateConnectionAsync(request, cancellationToken));

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueConnectionCheckAsync(
        Guid connectionId, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new ConnectionResource(connectionId), Permissions.ConnectionsWrite) is { } problem) return problem;
        var receipt = await application.QueueConnectionCheckAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueSchemaScanAsync(
        Guid connectionId, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new ConnectionResource(connectionId), Permissions.SchemaWrite) is { } problem) return problem;
        var receipt = await application.QueueSchemaScanAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<SchemaSnapshotResponse>, ProblemHttpResult>> GetSnapshotAsync(
        Guid connectionId, Guid snapshotId, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new ConnectionResource(connectionId), Permissions.SchemaRead) is { } problem) return problem;
        return TypedResults.Ok(await application.GetSnapshotAsync(connectionId, snapshotId, cancellationToken));
    }

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
        Guid planId, SavePlanRequest request, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (await AuthorizeResourceAsync(authorizationService, user, new PlanResource(planId), Permissions.PlansWrite) is { } problem) return problem;
        return TypedResults.Ok(await application.SavePlanAsync(planId, request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueuePlanSealAsync(
        Guid planId, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new PlanResource(planId), Permissions.PlansSeal) is { } problem) return problem;
        var receipt = await application.QueuePlanSealAsync(planId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> StartJobAsync(
        Guid planId, HttpRequest request, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency key is required.");
        if (await AuthorizeResourceAsync(authorizationService, user, new PlanResource(planId), Permissions.TransfersStart) is { } problem) return problem;

        var receipt = await application.StartJobAsync(planId, values.ToString(), cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetJobAsync(
        Guid jobId, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new JobResource(jobId), Permissions.TransfersRead) is { } problem) return problem;
        return TypedResults.Ok(await application.GetJobAsync(jobId, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueJobCommandAsync(
        Guid jobId, JobCommandRequest request, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(authorizationService, user, new JobResource(jobId), Permissions.TransfersWrite) is { } problem) return problem;
        var receipt = await application.QueueJobCommandAsync(jobId, request.Command, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }
}
