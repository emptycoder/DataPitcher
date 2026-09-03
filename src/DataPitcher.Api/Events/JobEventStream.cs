using System.Security.Claims;
using System.Text.Json;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Errors;
using DataPitcher.Core.Authorization;
using DataPitcher.Infrastructure.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace DataPitcher.Api.Events;

public static class JobEventStream
{
    public static void Map(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/{jobId:guid}/events", StreamAsync)
            .RequireAuthorization(ApiPolicyNames.TransfersRead)
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> StreamAsync(
        Guid jobId, HttpRequest request, HttpContext context, ClaimsPrincipal user, IAuthorizationService authorizationService,
        IJobEventReader reader, IJobEventSignal signal, IValidatedAccessTokenLifetime lifetime, [FromHeader(Name = "Last-Event-ID")] string? lastEventIdHeader, CancellationToken cancellationToken)
    {
        if (request.Query.Keys.Any(IsCredentialQueryKey) || !TryParseLastEventId(lastEventIdHeader, out var lastEventId))
            return ApiProblemMapper.Result(new(ApiErrorClass.Validation, new(null, null, null, null, null)), context);

        var resource = new JobResource(jobId);
        var authorization = await authorizationService.AuthorizeAsync(user, resource, new ResourcePermissionRequirement(Permissions.TransfersRead));
        if (!authorization.Succeeded)
            return AuthorizationFailureDiagnostics.IsIndeterminate(authorization.Failure) ? ApiAuthorizationResults.Indeterminate(context, resource) : ApiAuthorizationResults.Forbidden(context, resource);

        try
        {
            var page = await reader.ReadAfterAsync(jobId, lastEventId, cancellationToken);
            return new JobEventStreamResult(jobId, lastEventId ?? 0, page, reader, signal, lifetime.GetExpiryUtc(user), cancellationToken);
        }
        catch (EventCursorExpiredException exception)
        {
            return ApiProblemMapper.EventCursorExpired(context, exception.OldestAvailableEventId);
        }
    }

    private static bool IsCredentialQueryKey(string key) => key.Equals("access_token", StringComparison.OrdinalIgnoreCase) || key.Equals("token", StringComparison.OrdinalIgnoreCase) || key.Equals("authorization", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseLastEventId(string? header, out long? lastEventId)
    {
        lastEventId = null;
        if (header is null) return true;
        if (!long.TryParse(header, out var value) || value < 0) return false;
        lastEventId = value;
        return true;
    }

    private sealed class JobEventStreamResult(Guid jobId, long lastEventId, JobEventPage initialPage, IJobEventReader reader, IJobEventSignal signal, DateTimeOffset expiryUtc, CancellationToken requestAborted) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using var expiry = new CancellationTokenSource();
            var remaining = expiryUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) expiry.Cancel(); else expiry.CancelAfter(remaining);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, expiry.Token);
            var cancellationToken = cancelled.Token;
            var page = initialPage;
            try
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.StartAsync(cancellationToken);
                while (true)
                {
                    foreach (var jobEvent in page.Events)
                    {
                        await context.Response.WriteAsync($"id: {jobEvent.EventId}\nevent: {jobEvent.EventType}\ndata: {JsonSerializer.Serialize(jobEvent.Payload)}\n\n", cancellationToken);
                        lastEventId = jobEvent.EventId;
                    }
                    await context.Response.Body.FlushAsync(cancellationToken);
                    page = await reader.ReadAfterAsync(jobId, lastEventId, cancellationToken);
                    if (page.Events.Count == 0)
                    {
                        await signal.WaitAsync(jobId, lastEventId, cancellationToken);
                        page = await reader.ReadAfterAsync(jobId, lastEventId, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
