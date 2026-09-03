using System.Security.Claims;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Errors;
using DataPitcher.Api.Events;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DataPitcher.Api.Endpoints;

public sealed record JobCommandRequest(JobCommand Command);

public static class EndpointGroups
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/connections");
        WithStandardProblems(
            connections.MapGet("", ListConnectionsAsync).RequireAuthorization(ApiPolicyNames.ConnectionsRead)
        );
        WithStandardProblems(
            connections.MapPost("", CreateConnectionAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite)
        );
        WithStandardProblems(
            connections.MapPost("/test", TestConnectionAsync).RequireAuthorization(ApiPolicyNames.ConnectionsWrite)
        );
        WithStandardProblems(
            connections
                .MapPut("/{connectionId:guid}", UpdateConnectionAsync)
                .RequireAuthorization(ApiPolicyNames.ConnectionsWrite)
        );
        WithStandardProblems(
            connections
                .MapDelete("/{connectionId:guid}", DeleteConnectionAsync)
                .RequireAuthorization(ApiPolicyNames.ConnectionsWrite)
        );
        WithStandardProblems(
            connections
                .MapPost("/{connectionId:guid}/checks", QueueConnectionCheckAsync)
                .RequireAuthorization(ApiPolicyNames.ConnectionsWrite)
        );
        WithStandardProblems(
            connections
                .MapPost("/{connectionId:guid}/schema-scans", QueueSchemaScanAsync)
                .RequireAuthorization(ApiPolicyNames.SchemaWrite)
        );
        WithStandardProblems(
            connections
                .MapGet("/{connectionId:guid}/snapshots", ListSnapshotsAsync)
                .RequireAuthorization(ApiPolicyNames.SchemaRead)
        );
        WithStandardProblems(
                connections
                    .MapGet("/{connectionId:guid}/snapshots/{snapshotId:guid}", GetSnapshotAsync)
                    .RequireAuthorization(ApiPolicyNames.SchemaRead)
            )
            .ProducesProblem(StatusCodes.Status404NotFound);

        var operations = app.MapGroup("/api/operations");
        WithStandardProblems(operations.MapGet("/{operationId:guid}", GetOperationStatusAsync).RequireAuthorization());

        var selections = app.MapGroup("/api/selections");
        WithStandardProblems(
            selections
                .MapPut("/{selectionId:guid}", SaveSelectionAsync)
                .RequireAuthorization(ApiPolicyNames.SelectionsWrite)
        );
        WithStandardProblems(
            selections
                .MapDelete("/{selectionId:guid}", DeleteSelectionAsync)
                .RequireAuthorization(ApiPolicyNames.SelectionsWrite)
        );
        WithStandardProblems(
            selections
                .MapPost("/{selectionId:guid}/evaluations", QueueSelectionEvaluationAsync)
                .RequireAuthorization(ApiPolicyNames.SelectionsWrite)
        );

        var plans = app.MapGroup("/api/plans");
        WithStandardProblems(
            plans.MapPut("/{planId:guid}", SavePlanAsync).RequireAuthorization(ApiPolicyNames.PlansWrite)
        );
        WithStandardProblems(
            plans.MapPost("/{planId:guid}/seal", QueuePlanSealAsync).RequireAuthorization(ApiPolicyNames.PlansSeal)
        );
        WithStandardProblems(
            plans.MapGet("/{planId:guid}/review", GetPlanReviewAsync).RequireAuthorization(ApiPolicyNames.PlansRead)
        );
        WithStandardProblems(
            plans
                .MapPost("/{planId:guid}/inclusion-paths", GetPlanInclusionPathAsync)
                .RequireAuthorization(ApiPolicyNames.PlansRead)
        );
        WithStandardProblems(
            plans.MapPost("/{planId:guid}/jobs", StartJobAsync).RequireAuthorization(ApiPolicyNames.TransfersStart)
        );

        var jobs = app.MapGroup("/api/jobs");
        WithStandardProblems(jobs.MapGet("", ListJobsAsync).RequireAuthorization(ApiPolicyNames.TransfersRead));
        WithStandardProblems(
            jobs.MapGet("/{jobId:guid}", GetJobAsync).RequireAuthorization(ApiPolicyNames.TransfersRead)
        );
        WithStandardProblems(
            jobs.MapPost("/{jobId:guid}/commands", QueueJobCommandAsync)
                .RequireAuthorization(ApiPolicyNames.TransfersWrite)
        );
        JobEventStream.Map(jobs);
    }

    private static RouteHandlerBuilder WithStandardProblems(RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

    internal static async Task<ProblemHttpResult?> AuthorizeResourceAsync(
        HttpContext context,
        IAuthorizationService authorizationService,
        ClaimsPrincipal user,
        ApiResource resource,
        Permission permission
    )
    {
        var result = await authorizationService.AuthorizeAsync(
            user,
            resource,
            new ResourcePermissionRequirement(permission)
        );
        if (result.Succeeded)
            return null;
        return AuthorizationFailureDiagnostics.IsIndeterminate(result.Failure)
            ? ApiAuthorizationResults.Indeterminate(context, resource)
            : ApiAuthorizationResults.Forbidden(context, resource);
    }

    private static async Task<Ok<IReadOnlyList<ConnectionResponse>>> ListConnectionsAsync(
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    ) => TypedResults.Ok(await application.ListConnectionsAsync(cancellationToken));

    private static async Task<Results<Ok<ConnectionResponse>, ProblemHttpResult>> CreateConnectionAsync(
        CreateConnectionRequest request,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A connection string is required."
            );
        return TypedResults.Ok(await application.CreateConnectionAsync(request, cancellationToken));
    }

    private static async Task<Results<Ok<ConnectionTestResponse>, ProblemHttpResult>> TestConnectionAsync(
        ConnectionTestRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString) && request.ConnectionId is null)
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A connection string or an existing connection is required."
            );
        if (
            request.ConnectionId is Guid connectionId
            && await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.ConnectionsWrite
            )
                is { } problem
        )
            return problem;
        return TypedResults.Ok(await application.TestConnectionAsync(request, cancellationToken));
    }

    private static async Task<Results<Ok<ConnectionResponse>, ProblemHttpResult>> UpdateConnectionAsync(
        Guid connectionId,
        UpdateConnectionRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Display name is required."
            );
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.ConnectionsWrite
            ) is
            { } problem
        )
            return problem;
        try
        {
            return TypedResults.Ok(await application.UpdateConnectionAsync(connectionId, request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteConnectionAsync(
        Guid connectionId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.ConnectionsWrite
            ) is
            { } problem
        )
            return problem;
        try
        {
            await application.DeleteConnectionAsync(connectionId, ifMatch, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueConnectionCheckAsync(
        Guid connectionId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.ConnectionsWrite
            ) is
            { } problem
        )
            return problem;
        var receipt = await application.QueueConnectionCheckAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueSchemaScanAsync(
        Guid connectionId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.SchemaWrite
            ) is
            { } problem
        )
            return problem;
        var receipt = await application.QueueSchemaScanAsync(connectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<
        Results<Ok<IReadOnlyList<SchemaSnapshotSummaryResponse>>, ProblemHttpResult>
    > ListSnapshotsAsync(
        Guid connectionId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.SchemaRead
            ) is
            { } problem
        )
            return problem;
        return TypedResults.Ok(await application.ListSnapshotsAsync(connectionId, cancellationToken));
    }

    private static async Task<Results<Ok<SchemaSnapshotResponse>, ProblemHttpResult>> GetSnapshotAsync(
        Guid connectionId,
        Guid snapshotId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(connectionId),
                Permissions.SchemaRead
            ) is
            { } problem
        )
            return problem;
        var snapshot = await application.FindSnapshotAsync(connectionId, snapshotId, cancellationToken);
        return snapshot is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Schema snapshot not found.")
            : TypedResults.Ok(snapshot);
    }

    private static async Task<
        Results<Ok<OperationStatusResponse>, NotFound, ProblemHttpResult>
    > GetOperationStatusAsync(
        Guid operationId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        var status = await application.GetOperationStatusAsync(operationId, cancellationToken);
        if (status is null)
            return TypedResults.NotFound();
        ApiResource resource = status.ConnectionId is { } connectionId
            ? new ConnectionResource(connectionId)
            : new JobResource(status.JobId!.Value);
        var permission = status.ConnectionId is null ? Permissions.TransfersRead : Permissions.SchemaRead;
        if (await AuthorizeResourceAsync(context, authorizationService, user, resource, permission) is { } problem)
            return problem;
        return TypedResults.Ok(status);
    }

    private static async Task<Results<Ok<SelectionResponse>, ProblemHttpResult>> SaveSelectionAsync(
        Guid selectionId,
        SaveSelectionRequest request,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        return TypedResults.Ok(await application.SaveSelectionAsync(selectionId, request, cancellationToken));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSelectionAsync(
        Guid selectionId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        try
        {
            await application.DeleteSelectionAsync(selectionId, ifMatch, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
    }

    private static async Task<Accepted<OperationReceiptResponse>> QueueSelectionEvaluationAsync(
        Guid selectionId,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        var receipt = await application.QueueSelectionEvaluationAsync(selectionId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<PlanResponse>, ProblemHttpResult>> SavePlanAsync(
        Guid planId,
        SavePlanRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.IfMatch))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "If-Match is required.");
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new PlanResource(planId),
                Permissions.PlansWrite
            ) is
            { } problem
        )
            return problem;
        if (
            request.SourceConnectionId is Guid sourceConnectionId
            && await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(sourceConnectionId),
                Permissions.ConnectionsRead
            )
                is { } sourceProblem
        )
            return sourceProblem;
        if (
            request.TargetConnectionId is Guid targetConnectionId
            && await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new ConnectionResource(targetConnectionId),
                Permissions.ConnectionsRead
            )
                is { } targetProblem
        )
            return targetProblem;
        return TypedResults.Ok(await application.SavePlanAsync(planId, request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueuePlanSealAsync(
        Guid planId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new PlanResource(planId),
                Permissions.PlansSeal
            ) is
            { } problem
        )
            return problem;
        var receipt = await application.QueuePlanSealAsync(planId, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }

    private static async Task<Results<Ok<PlanReviewResponse>, ProblemHttpResult>> GetPlanReviewAsync(
        Guid planId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new PlanResource(planId),
                Permissions.PlansRead
            ) is
            { } problem
        )
            return problem;
        return TypedResults.Ok(await application.GetPlanReviewAsync(planId, cancellationToken));
    }

    private static async Task<Results<Ok<InclusionPathResponse>, ProblemHttpResult>> GetPlanInclusionPathAsync(
        Guid planId,
        InclusionPathRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.Table) || string.IsNullOrWhiteSpace(request.StableKey))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Table and stable key are required."
            );
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new PlanResource(planId),
                Permissions.PlansRead
            ) is
            { } problem
        )
            return problem;
        return TypedResults.Ok(await application.GetPlanInclusionPathAsync(planId, request, cancellationToken));
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> StartJobAsync(
        Guid planId,
        HttpRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            !request.Headers.TryGetValue("Idempotency-Key", out var values)
            || string.IsNullOrWhiteSpace(values.ToString())
        )
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency key is required."
            );
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new PlanResource(planId),
                Permissions.TransfersStart
            ) is
            { } problem
        )
            return problem;

        try
        {
            var receipt = await application.StartJobAsync(planId, values.ToString(), cancellationToken);
            return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
        }
        catch (PlanNotFoundException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: exception.Message);
        }
        catch (PlanNotSealedException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetJobAsync(
        Guid jobId,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new JobResource(jobId),
                Permissions.TransfersRead
            ) is
            { } problem
        )
            return problem;
        return TypedResults.Ok(await application.GetJobAsync(jobId, cancellationToken));
    }

    private static async Task<Ok<IReadOnlyList<JobSummaryResponse>>> ListJobsAsync(
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        var jobs = await application.ListJobsAsync(cancellationToken);
        var visible = new List<JobSummaryResponse>();
        foreach (var job in jobs)
        {
            var authorization = await authorizationService.AuthorizeAsync(
                user,
                new JobResource(job.JobId),
                new ResourcePermissionRequirement(Permissions.TransfersRead)
            );
            if (authorization.Succeeded)
                visible.Add(job);
        }
        return TypedResults.Ok<IReadOnlyList<JobSummaryResponse>>(visible);
    }

    private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> QueueJobCommandAsync(
        Guid jobId,
        JobCommandRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService,
        IDataPitcherApplication application,
        CancellationToken cancellationToken
    )
    {
        if (
            await AuthorizeResourceAsync(
                context,
                authorizationService,
                user,
                new JobResource(jobId),
                Permissions.TransfersWrite
            ) is
            { } problem
        )
            return problem;
        var receipt = await application.QueueJobCommandAsync(jobId, request.Command, cancellationToken);
        return TypedResults.Accepted(receipt.StatusUri.ToString(), receipt);
    }
}
