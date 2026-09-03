using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Events;
using DataPitcher.Api.Errors;
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
        WithStandardProblems(connections.MapGet("", ListConnectionsAsync).RequireAuthorization(ApiPolicyNames.ConnectionsRead));
        WithStandardProblems(connections.MapPost("", CreateConnectionAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite));
        WithStandardProblems(connections.MapPost("/{connectionId:guid}/checks", QueueConnectionCheckAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite));
        WithStandardProblems(connections.MapPost("/{connectionId:guid}/schema-scans", QueueSchemaScanAsync).RequireAuthorization(ApiPolicyNames.SchemaWrite));
        WithStandardProblems(connections.MapGet("/{connectionId:guid}/snapshots/{snapshotId:guid}", GetSnapshotAsync).RequireAuthorization(ApiPolicyNames.SchemaRead));

        var selections = app.MapGroup("/api/selections");
        WithStandardProblems(selections.MapPut("/{selectionId:guid}", SaveSelectionAsync).RequireAuthorization(ApiPolicyNames.SelectionsWrite));
        WithStandardProblems(selections.MapPost("/{selectionId:guid}/evaluations", QueueSelectionEvaluationAsync).RequireAuthorization(ApiPolicyNames.SelectionsWrite));

        var plans = app.MapGroup("/api/plans");
        WithStandardProblems(plans.MapPut("/{planId:guid}", SavePlanAsync).RequireAuthorization(ApiPolicyNames.PlansWrite));
        WithStandardProblems(plans.MapPost("/{planId:guid}/seal", QueuePlanSealAsync).RequireAuthorization(ApiPolicyNames.PlansSeal));
        WithStandardProblems(plans.MapGet("/{planId:guid}/review", GetPlanReviewAsync).RequireAuthorization(ApiPolicyNames.PlansRead));
        WithStandardProblems(plans.MapPost("/{planId:guid}/inclusion-paths", GetPlanInclusionPathAsync).RequireAuthorization(ApiPolicyNames.PlansRead));
        WithStandardProblems(plans.MapPost("/{planId:guid}/jobs", StartJobAsync).RequireAuthorization(ApiPolicyNames.TransfersStart));

        var jobs = app.MapGroup("/api/jobs");
        WithStandardProblems(jobs.MapGet("/{jobId:guid}", GetJobAsync).RequireAuthorization(ApiPolicyNames.TransfersRead));
        WithStandardProblems(jobs.MapPost("/{jobId:guid}/commands", QueueJobCommandAsync).RequireAuthorization(ApiPolicyNames.TransfersWrite));
        JobEventStream.Map(jobs);
    }

    private static RouteHandlerBuilder WithStandardProblems(RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

    private static async Task<ProblemHttpResult?> AuthorizeResourceAsync(
        HttpContext context, IAuthorizationService authorizationService, ClaimsPrincipal user, ApiResource resource, Permission permission)
    {
        var result = await authorizationService.AuthorizeAsync(user, resource, new ResourcePermissionRequirement(permission));
        if (result.Succeeded) return null;
        return AuthorizationFailureDiagnostics.IsIndeterminate(result.Failure) ? ApiAuthorizationResults.Indeterminate(context, resource) : ApiAuthorizationResults.Forbidden(context, resource);
    }

    private static async Task<Ok<IReadOnlyList<ConnectionResponse>>> ListConnectionsAsync(
        IDataPitcherApplication application, CancellationToken cancellationToken) =>
        TypedResults.Ok(await application.ListConnectionsAsync(cancellationToken));

    private static async Task<Results<Ok<ConnectionResponse>, ProblemHttpResult>> CreateConnectionAsync(
        CreateConnectionRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        return TypedResults.Ok(await application.CreateConnectionAsync(request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueConnectionCheckAsync(
        Guid connectionId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new ConnectionResource(connectionId), Permissions.ConnectionsWrite) is { } problem) return problem;
        var receipt = await application.QueueConnectionCheckAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueSchemaScanAsync(
        Guid connectionId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new ConnectionResource(connectionId), Permissions.SchemaWrite) is { } problem) return problem;
        var receipt = await application.QueueSchemaScanAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<SchemaSnapshotResponse>, ProblemHttpResult>> GetSnapshotAsync(
        Guid connectionId, Guid snapshotId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new ConnectionResource(connectionId), Permissions.SchemaRead) is { } problem) return problem;
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
        Guid planId, SavePlanRequest request, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (await AuthorizeResourceAsync(context, authorizationService, user, new PlanResource(planId), Permissions.PlansWrite) is { } problem) return problem;
        return TypedResults.Ok(await application.SavePlanAsync(planId, request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueuePlanSealAsync(
        Guid planId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new PlanResource(planId), Permissions.PlansSeal) is { } problem) return problem;
        var receipt = await application.QueuePlanSealAsync(planId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<PlanReviewResponse>, ProblemHttpResult>> GetPlanReviewAsync(
        Guid planId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new PlanResource(planId), Permissions.PlansRead) is { } problem) return problem;
        return TypedResults.Ok(await application.GetPlanReviewAsync(planId, cancellationToken));
    }

    private static async Task<Results<Ok<InclusionPathResponse>, ProblemHttpResult>> GetPlanInclusionPathAsync(
        Guid planId, InclusionPathRequest request, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Table) || string.IsNullOrWhiteSpace(request.StableKey))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Table and stable key are required.");
        if (await AuthorizeResourceAsync(context, authorizationService, user, new PlanResource(planId), Permissions.PlansRead) is { } problem) return problem;
        return TypedResults.Ok(await application.GetPlanInclusionPathAsync(planId, request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> StartJobAsync(
        Guid planId, HttpRequest request, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency key is required.");
        if (await AuthorizeResourceAsync(context, authorizationService, user, new PlanResource(planId), Permissions.TransfersStart) is { } problem) return problem;

        var receipt = await application.StartJobAsync(planId, values.ToString(), cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetJobAsync(
        Guid jobId, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new JobResource(jobId), Permissions.TransfersRead) is { } problem) return problem;
        return TypedResults.Ok(await application.GetJobAsync(jobId, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueJobCommandAsync(
        Guid jobId, JobCommandRequest request, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService, IDataPitcherApplication application, CancellationToken cancellationToken)
    {
        if (await AuthorizeResourceAsync(context, authorizationService, user, new JobResource(jobId), Permissions.TransfersWrite) is { } problem) return problem;
        var receipt = await application.QueueJobCommandAsync(jobId, request.Command, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }
}
