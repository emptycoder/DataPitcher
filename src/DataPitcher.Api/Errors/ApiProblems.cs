using System.Text.Json;
using DataPitcher.Api.Contracts;
using DataPitcher.Application.Events;
using DataPitcher.Application.Worker;
using DataPitcher.ControlStore;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DataPitcher.Api.Errors;

public enum ApiErrorClass
{
    Validation,
    Unauthenticated,
    Forbidden,
    IdentityProviderUnavailable,
    InvalidToken,
    TenantRejected,
    GroupResolutionFailed,
    AuthenticationConfiguration,
    Connection,
    SchemaDrift,
    UnsupportedProviderFeature,
    QuerySyntax,
    QueryTimeout,
    SourceIntegrity,
    TargetConflict,
    TypeConversion,
    ConstraintCycle,
    BulkWrite,
    TransientDatabaseFailure,
    Cancelled,
    Verification,
    Internal,
}

public sealed record ApiFault(ApiErrorClass ErrorClass, ResourceIdentifiers Resources);

public static class ApiProblemMapper
{
    private sealed record Definition(int Status, string Code, string Title, string Detail, string Type);

    public static ProblemDetails Map(ApiFault fault, string correlationId)
    {
        var definition = DefinitionFor(fault.ErrorClass);
        var problem = new ProblemDetails
        {
            Status = definition.Status,
            Title = definition.Title,
            Detail = definition.Detail,
            Type = definition.Type,
        };
        problem.Extensions["code"] = definition.Code;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["resources"] = fault.Resources;
        return problem;
    }

    public static ProblemHttpResult Result(ApiFault fault, HttpContext context) =>
        TypedResults.Problem(Map(fault, CorrelationId(context)));

    public static ProblemHttpResult EventCursorExpired(HttpContext context, long oldestAvailableEventId)
    {
        var problem = Map(new(ApiErrorClass.Validation, new(null, null, null, null, null)), CorrelationId(context));
        problem.Status = StatusCodes.Status409Conflict;
        problem.Title = "Event cursor expired";
        problem.Detail = "The retained event history no longer includes the supplied cursor.";
        problem.Type = "urn:datapitcher:event-cursor-expired";
        problem.Extensions["code"] = "event_cursor_expired";
        problem.Extensions["reloadRequired"] = true;
        problem.Extensions["oldestAvailableEventId"] = oldestAvailableEventId;
        return TypedResults.Problem(problem);
    }

    public static async Task WriteAsync(HttpContext context, ApiFault fault, CancellationToken cancellationToken)
    {
        var problem = Map(fault, CorrelationId(context));
        context.Response.StatusCode = problem.Status.GetValueOrDefault(StatusCodes.Status500InternalServerError);
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: cancellationToken);
    }

    private static string CorrelationId(HttpContext context) =>
        Guid.TryParse(context.Request.Headers["X-Correlation-ID"], out _)
            ? context.Request.Headers["X-Correlation-ID"].ToString()
            : Guid.NewGuid().ToString();

    private static Definition DefinitionFor(ApiErrorClass errorClass) =>
        errorClass switch
        {
            ApiErrorClass.Validation => new(
                400,
                "validation_failed",
                "Validation failed",
                "The request is not valid.",
                "urn:datapitcher:validation"
            ),
            ApiErrorClass.Unauthenticated => new(
                401,
                "unauthenticated",
                "Authentication required",
                "Authentication is required for this operation.",
                "urn:datapitcher:unauthenticated"
            ),
            ApiErrorClass.Forbidden => new(
                403,
                "authorization_denied",
                "Authorization denied",
                "You are not allowed to perform this operation.",
                "urn:datapitcher:forbidden"
            ),
            ApiErrorClass.IdentityProviderUnavailable => new(
                503,
                "identity_provider_unavailable",
                "Identity provider unavailable",
                "Authentication cannot be completed now.",
                "urn:datapitcher:identity-provider-unavailable"
            ),
            ApiErrorClass.InvalidToken => new(
                401,
                "invalid_token",
                "Invalid credentials",
                "The supplied credentials are not valid.",
                "urn:datapitcher:invalid-token"
            ),
            ApiErrorClass.TenantRejected => new(
                403,
                "tenant_rejected",
                "Tenant rejected",
                "This tenant is not allowed.",
                "urn:datapitcher:tenant-rejected"
            ),
            ApiErrorClass.GroupResolutionFailed => new(
                503,
                "authorization_indeterminate",
                "Authorization unavailable",
                "Authorization cannot be determined now.",
                "urn:datapitcher:authorization-indeterminate"
            ),
            ApiErrorClass.AuthenticationConfiguration => new(
                500,
                "authentication_configuration_error",
                "Authentication configuration error",
                "Authentication is unavailable.",
                "urn:datapitcher:authentication-configuration"
            ),
            ApiErrorClass.Connection => new(
                502,
                "connection_failed",
                "Connection failed",
                "The database connection could not be used.",
                "urn:datapitcher:connection"
            ),
            ApiErrorClass.SchemaDrift => new(
                409,
                "schema_drift",
                "Schema drift",
                "The schema changed since it was inspected.",
                "urn:datapitcher:schema-drift"
            ),
            ApiErrorClass.UnsupportedProviderFeature => new(
                422,
                "unsupported_provider_feature",
                "Unsupported provider feature",
                "The selected provider capability is unavailable.",
                "urn:datapitcher:unsupported-provider-feature"
            ),
            ApiErrorClass.QuerySyntax => new(
                400,
                "query_syntax_invalid",
                "Invalid query",
                "The selection query is not valid.",
                "urn:datapitcher:query-syntax"
            ),
            ApiErrorClass.QueryTimeout => new(
                504,
                "query_timeout",
                "Query timeout",
                "The database query did not finish in time.",
                "urn:datapitcher:query-timeout"
            ),
            ApiErrorClass.SourceIntegrity => new(
                409,
                "source_integrity_failed",
                "Source integrity failure",
                "The source data no longer meets the plan requirements.",
                "urn:datapitcher:source-integrity"
            ),
            ApiErrorClass.TargetConflict => new(
                409,
                "target_conflict",
                "Target conflict",
                "The target conflicts with the requested transfer.",
                "urn:datapitcher:target-conflict"
            ),
            ApiErrorClass.TypeConversion => new(
                422,
                "type_conversion_failed",
                "Type conversion failed",
                "A required value conversion is unsafe.",
                "urn:datapitcher:type-conversion"
            ),
            ApiErrorClass.ConstraintCycle => new(
                422,
                "constraint_cycle",
                "Constraint cycle",
                "The planned relationship cycle cannot be transferred safely.",
                "urn:datapitcher:constraint-cycle"
            ),
            ApiErrorClass.BulkWrite => new(
                502,
                "bulk_write_failed",
                "Bulk write failed",
                "The target write could not be completed.",
                "urn:datapitcher:bulk-write"
            ),
            ApiErrorClass.TransientDatabaseFailure => new(
                503,
                "transient_database_failure",
                "Temporary database failure",
                "The database is temporarily unavailable.",
                "urn:datapitcher:transient-database"
            ),
            ApiErrorClass.Cancelled => new(
                409,
                "operation_cancelled",
                "Operation cancelled",
                "The operation was cancelled.",
                "urn:datapitcher:cancelled"
            ),
            ApiErrorClass.Verification => new(
                422,
                "verification_failed",
                "Verification failed",
                "The transfer did not pass verification.",
                "urn:datapitcher:verification"
            ),
            ApiErrorClass.Internal => new(
                500,
                "internal_error",
                "Internal error",
                "The operation could not be completed.",
                "urn:datapitcher:internal"
            ),
            _ => new(
                500,
                "internal_error",
                "Internal error",
                "The operation could not be completed.",
                "urn:datapitcher:internal"
            ),
        };
}

public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var fault = new ApiFault(Classify(context, exception), new ResourceIdentifiers(null, null, null, null, null));
        await ApiProblemMapper.WriteAsync(context, fault, cancellationToken);
        return true;
    }

    private static ApiErrorClass Classify(HttpContext context, Exception exception) =>
        exception switch
        {
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => ApiErrorClass.Cancelled,
            ArgumentException => ApiErrorClass.Validation,
            InvalidJobStateTransitionException => ApiErrorClass.TargetConflict,
            RootConflictException => ApiErrorClass.TargetConflict,
            TargetFenceLostException => ApiErrorClass.TargetConflict,
            BlockedTableException => ApiErrorClass.UnsupportedProviderFeature,
            ManifestSealMismatchException => ApiErrorClass.SourceIntegrity,
            TransferAttemptException => ApiErrorClass.Internal,
            NonResumableInterruptedException => ApiErrorClass.Internal,
            SimulatedWorkerFaultException => ApiErrorClass.Internal,
            _ => ApiErrorClass.Internal,
        };
}
