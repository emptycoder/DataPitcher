using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Errors;
using DataPitcher.Core.Closure;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Schema;
using DataPitcher.Infrastructure.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class ProblemDetailsTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public static IEnumerable<object[]> Expected()
    {
        yield return [ApiErrorClass.Validation, 400, "validation_failed", "The request is not valid."];
        yield return [ApiErrorClass.Unauthenticated, 401, "unauthenticated", "Authentication is required for this operation."];
        yield return [ApiErrorClass.Forbidden, 403, "authorization_denied", "You are not allowed to perform this operation."];
        yield return [ApiErrorClass.IdentityProviderUnavailable, 503, "identity_provider_unavailable", "Authentication cannot be completed now."];
        yield return [ApiErrorClass.InvalidToken, 401, "invalid_token", "The supplied credentials are not valid."];
        yield return [ApiErrorClass.TenantRejected, 403, "tenant_rejected", "This tenant is not allowed."];
        yield return [ApiErrorClass.GroupResolutionFailed, 503, "authorization_indeterminate", "Authorization cannot be determined now."];
        yield return [ApiErrorClass.AuthenticationConfiguration, 500, "authentication_configuration_error", "Authentication is unavailable."];
        yield return [ApiErrorClass.Connection, 502, "connection_failed", "The database connection could not be used."];
        yield return [ApiErrorClass.SchemaDrift, 409, "schema_drift", "The schema changed since it was inspected."];
        yield return [ApiErrorClass.UnsupportedProviderFeature, 422, "unsupported_provider_feature", "The selected provider capability is unavailable."];
        yield return [ApiErrorClass.QuerySyntax, 400, "query_syntax_invalid", "The selection query is not valid."];
        yield return [ApiErrorClass.QueryTimeout, 504, "query_timeout", "The database query did not finish in time."];
        yield return [ApiErrorClass.SourceIntegrity, 409, "source_integrity_failed", "The source data no longer meets the plan requirements."];
        yield return [ApiErrorClass.TargetConflict, 409, "target_conflict", "The target conflicts with the requested transfer."];
        yield return [ApiErrorClass.TypeConversion, 422, "type_conversion_failed", "A required value conversion is unsafe."];
        yield return [ApiErrorClass.ConstraintCycle, 422, "constraint_cycle", "The planned relationship cycle cannot be transferred safely."];
        yield return [ApiErrorClass.BulkWrite, 502, "bulk_write_failed", "The target write could not be completed."];
        yield return [ApiErrorClass.TransientDatabaseFailure, 503, "transient_database_failure", "The database is temporarily unavailable."];
        yield return [ApiErrorClass.Cancelled, 409, "operation_cancelled", "The operation was cancelled."];
        yield return [ApiErrorClass.Verification, 422, "verification_failed", "The transfer did not pass verification."];
        yield return [ApiErrorClass.Internal, 500, "internal_error", "The operation could not be completed."];
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void ApiProblemMapper_MapsEveryArchitectureClassToFixedSafeDetails(ApiErrorClass errorClass, int status, string code, string detail)
    {
        var resources = new ResourceIdentifiers(Guid.NewGuid(), null, null, Guid.NewGuid(), null);

        var problem = ApiProblemMapper.Map(new ApiFault(errorClass, resources), "correlation-01");

        Assert.Equal(status, problem.Status);
        Assert.Equal(detail, problem.Detail);
        Assert.Equal(code, problem.Extensions["code"]);
        Assert.Equal("correlation-01", problem.Extensions["correlationId"]);
        Assert.Same(resources, problem.Extensions["resources"]);
    }

    [Fact]
    public async Task ExceptionResponse_UsesAValidatedCorrelationIdentifier()
    {
        var correlationId = Guid.NewGuid().ToString();
        _factory.Application.Delay = _ => Task.FromException(new InvalidOperationException("untrusted failure"));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
            request.Headers.Add("X-Correlation-ID", correlationId);
            using var response = await _client.SendAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var problem = await ReadProblemAsync(response);
            Assert.Equal(correlationId, problem.GetProperty("correlationId").GetString());
        }
        finally
        {
            _factory.Application.Delay = null;
        }
    }

    [Fact]
    public async Task ExceptionResponse_DoesNotEchoAnInvalidCorrelationIdentifier()
    {
        const string invalidCorrelationId = "operator-token-redaction-sentinel";
        _factory.Application.Delay = _ => Task.FromException(new InvalidOperationException("untrusted failure"));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
            request.Headers.Add("X-Correlation-ID", invalidCorrelationId);
            using var response = await _client.SendAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var problem = await ReadProblemAsync(response);
            Assert.NotEqual(invalidCorrelationId, problem.GetProperty("correlationId").GetString());
            Assert.True(Guid.TryParse(problem.GetProperty("correlationId").GetString(), out _));
        }
        finally
        {
            _factory.Application.Delay = null;
        }
    }

    [Fact]
    public async Task SuccessfulResponse_DoesNotContainCredentialSentinels()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
        request.Headers.Add("X-Test-Raw-Claim", "raw-claim-redaction-sentinel");
        using var response = await _client.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        AssertNoSentinel(body);
        AssertNoSentinel(ResponseHeaders(response));
    }

    [Fact]
    public async Task ExceptionResponse_RedactsCredentialsFromProblemDetailsAndHeaders()
    {
        _factory.Application.Delay = _ => Task.FromException(new InvalidOperationException(string.Join(' ', ForbiddenSentinels)));
        try
        {
            using var response = await _client.GetAsync("/api/connections", CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            AssertNoSentinel(body);
            AssertNoSentinel(ResponseHeaders(response));
            using var document = JsonDocument.Parse(body);
            Assert.Equal("internal_error", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            _factory.Application.Delay = null;
        }
    }

    [Fact]
    public async Task ApiExceptionHandler_MapsArgumentExceptionToValidation()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, new ArgumentException("unsafe input"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        Assert.Equal("validation_failed", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiExceptionHandler_MapsRequestCancellationToCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        Assert.Equal("operation_cancelled", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiExceptionHandler_MapsUncancelledOperationCancellationToInternal()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        Assert.Equal("internal_error", document.RootElement.GetProperty("code").GetString());
    }

    public static IEnumerable<object[]> KnownDomainAndApplicationExceptions()
    {
        var table = new TableDefinition("dbo", "Orders", [], null, []);
        var row = new RowAddress(table, new StableKey([new KeyComponent("Id", 1)]));
        yield return [new InvalidJobStateTransitionException(JobState.Draft, JobState.Succeeded), "target_conflict"];
        yield return [new RootConflictException(row), "target_conflict"];
        yield return [new BlockedTableException(table), "unsupported_provider_feature"];
        yield return [new TargetFenceLostException(), "target_conflict"];
        yield return [new ManifestSealMismatchException(), "source_integrity_failed"];
        yield return [new TransferAttemptException(CommitDisposition.NotCommitted, new InvalidOperationException()), "internal_error"];
        yield return [new NonResumableInterruptedException(), "internal_error"];
        yield return [new SimulatedWorkerFaultException(TransferFaultPoint.PermanentFailure), "internal_error"];
    }

    [Theory]
    [MemberData(nameof(KnownDomainAndApplicationExceptions))]
    public async Task ApiExceptionHandler_MapsKnownDomainAndApplicationExceptions(Exception exception, string code)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void RoutedEndpoints_DeclareProblemDetailsMetadata()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null);

        foreach (var endpoint in endpoints)
        {
            var hasProblemMetadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Any(metadata => metadata.ContentTypes.Contains("application/problem+json", StringComparer.Ordinal));
            var route = endpoint is RouteEndpoint routeEndpoint ? routeEndpoint.RoutePattern.RawText : "(no route pattern)";
            Assert.True(hasProblemMetadata, $"{endpoint.DisplayName} ({route}) must declare Problem Details metadata.");
        }
    }

    private static readonly string[] ForbiddenSentinels =
    [
        "password-redaction-sentinel",
        "token-redaction-sentinel",
        "client-secret-redaction-sentinel",
        "Server=redaction-sentinel;Password=redaction-sentinel",
        "secret-reference-redaction-sentinel",
        "raw-claim-redaction-sentinel",
    ];

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string ResponseHeaders(HttpResponseMessage response) =>
        string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));

    private static void AssertNoSentinel(string value)
    {
        foreach (var sentinel in ForbiddenSentinels)
            Assert.DoesNotContain(sentinel, value, StringComparison.Ordinal);
    }
}
