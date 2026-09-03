using System.Net;
using System.Net.Http.Json;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;
using DataPitcher.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class EndpointAuthorizationSafetyNetTests(ApiWebApplicationFactory factory)
    : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public void ApiBoundaryTestAuthenticationScheme_IsClearlyScoped()
    {
        Assert.Equal("ApiBoundaryAuthorizationTest", TestAuthenticationHandler.SchemeName);
    }

    [Fact]
    public void RoutedEndpoints_AreExplicitlyProtectedOrJustifiedAnonymous()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope
            .ServiceProvider.GetRequiredService<EndpointDataSource>()
            .Endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null);

        foreach (var endpoint in endpoints)
        {
            var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count != 0;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var justification = endpoint.Metadata.GetMetadata<AnonymousAccessJustificationMetadata>();
            var valid = authorized ^ anonymous && (!anonymous || !string.IsNullOrWhiteSpace(justification?.Reason));
            var route = endpoint is RouteEndpoint routeEndpoint
                ? routeEndpoint.RoutePattern.RawText
                : "(no route pattern)";
            Assert.True(valid, $"{endpoint.DisplayName} ({route}) must have exactly one access mode.");
        }
    }

    [Fact]
    public void RoutedEndpoints_AnonymousAccessIsLimitedToLivenessAndProviderDiscovery()
    {
        using var scope = _factory.Services.CreateScope();
        var endpoints = scope
            .ServiceProvider.GetRequiredService<EndpointDataSource>()
            .Endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint is RouteEndpoint routeEndpoint ? routeEndpoint.RoutePattern.RawText : null)
            .ToArray();

        Assert.Equal(["/api/providers", "/health/live"], endpoints.OrderBy(route => route, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProtectedEndpoint_WhenCredentialsAreMissing_Returns401ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
        request.Headers.Add("X-Test-Unauthenticated", "true");
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("unauthenticated", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_WhenIdentityLacksPermission_Returns403ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections");
        request.Headers.Add("X-Test-Permissions", Permissions.PlansRead.Value);
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("authorization_denied", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResourceAuthorization_GrantsOnlyTheSpecificJobIdentifier()
    {
        var grantedJobId = Guid.NewGuid();
        var deniedJobId = Guid.NewGuid();

        using var grantedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{grantedJobId}");
        grantedRequest.Headers.Add("X-Test-Denied-Resource", deniedJobId.ToString());
        using var grantedResponse = await _client.SendAsync(grantedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, grantedResponse.StatusCode);

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{deniedJobId}");
        deniedRequest.Headers.Add("X-Test-Denied-Resource", deniedJobId.ToString());
        using var deniedResponse = await _client.SendAsync(deniedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }

    [Fact]
    public async Task JobList_ExcludesJobsWithoutAResourceGrant()
    {
        var job = new JobSummaryResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Running",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0,
            0
        );
        _factory.Application.JobSummaries = [job];
        _factory.Grants.AllowJob(job.JobId, false);
        try
        {
            using var response = await _client.GetAsync("/api/jobs", CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var jobs = await response.Content.ReadFromJsonAsync<List<JobSummaryResponse>>();
            Assert.Empty(jobs!);
        }
        finally
        {
            _factory.Application.JobSummaries = null;
        }
    }

    [Fact]
    public async Task OperationStatus_UsesTheOperationResourceGrant()
    {
        var connectionId = Guid.NewGuid();
        _factory.Application.OperationStatus = new(
            Guid.NewGuid(),
            "schema-scan",
            "Queued",
            false,
            false,
            null,
            connectionId,
            null,
            null,
            null
        );
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/operations/{Guid.NewGuid()}");
            request.Headers.Add("X-Test-Denied-Resource", connectionId.ToString());
            using var response = await _client.SendAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            _factory.Application.OperationStatus = null;
        }
    }

    [Fact]
    public async Task OperationStatus_UsesTheJobResourceGrant()
    {
        var jobId = Guid.NewGuid();
        _factory.Application.OperationStatus = new(
            Guid.NewGuid(),
            "job",
            "Queued",
            false,
            false,
            null,
            null,
            null,
            Guid.NewGuid(),
            jobId
        );
        _factory.Grants.AllowJob(jobId, false);
        try
        {
            using var response = await _client.GetAsync($"/api/operations/{Guid.NewGuid()}", CancellationToken.None);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            _factory.Application.OperationStatus = null;
        }
    }

    [Fact]
    public async Task ResourceAuthorization_GrantsOnlyTheSpecificConnectionIdentifier()
    {
        var deniedConnectionId = Guid.NewGuid();
        using var deniedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/connections/{deniedConnectionId}/checks"
        );
        deniedRequest.Headers.Add("X-Test-Denied-Resource", deniedConnectionId.ToString());
        using var deniedResponse = await _client.SendAsync(deniedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var grantedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/connections/{Guid.NewGuid()}/checks");
        grantedRequest.Headers.Add("X-Test-Denied-Resource", deniedConnectionId.ToString());
        using var grantedResponse = await _client.SendAsync(grantedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, grantedResponse.StatusCode);
    }

    [Fact]
    public async Task ResourceAuthorization_GrantsOnlyTheSpecificPlanIdentifier()
    {
        var deniedPlanId = Guid.NewGuid();
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{deniedPlanId}/seal");
        deniedRequest.Headers.Add("X-Test-Denied-Resource", deniedPlanId.ToString());
        using var deniedResponse = await _client.SendAsync(deniedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var grantedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{Guid.NewGuid()}/seal");
        grantedRequest.Headers.Add("X-Test-Denied-Resource", deniedPlanId.ToString());
        using var grantedResponse = await _client.SendAsync(grantedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, grantedResponse.StatusCode);
    }

    [Fact]
    public async Task ResourceAuthorization_WhenPermissionIsMissing_DoesNotReadResourceGrant()
    {
        var calls = _factory.Grants.JobAuthorizationCalls;
        var context = new AuthorizationHandlerContext(
            [new ResourcePermissionRequirement(Permissions.TransfersRead)],
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            new JobResource(Guid.NewGuid())
        );

        await new ResourceAuthorizationHandler(_factory.Grants).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(calls, _factory.Grants.JobAuthorizationCalls);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("schema-scan")]
    [InlineData("plan-save")]
    [InlineData("job-start")]
    [InlineData("job-command")]
    public async Task ResourceAuthorization_WhenRouteResourceIsDenied_DoesNotInvokeTheApplication(string operation)
    {
        var resourceId = Guid.NewGuid();
        var invocationCount = _factory.Application.Invocations.Count;
        using var request = operation switch
        {
            "snapshot" => new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/connections/{resourceId}/snapshots/{Guid.NewGuid()}"
            ),
            "schema-scan" => new HttpRequestMessage(HttpMethod.Post, $"/api/connections/{resourceId}/schema-scans"),
            "plan-save" => new HttpRequestMessage(HttpMethod.Put, $"/api/plans/{resourceId}")
            {
                Content = JsonContent.Create(new SavePlanRequest("Plan", null, "etag-1")),
            },
            "job-start" => new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{resourceId}/jobs"),
            "job-command" => new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{resourceId}/commands")
            {
                Content = JsonContent.Create(new JobCommandRequest(JobCommand.Pause)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (operation == "job-start")
            request.Headers.Add("Idempotency-Key", "request-authorization");
        request.Headers.Add("X-Test-Denied-Resource", resourceId.ToString());

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(invocationCount, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task HealthLive_IsReachableWithoutCredentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Test-Unauthenticated", "true");
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Providers_IsReachableWithoutCredentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/providers");
        request.Headers.Add("X-Test-Unauthenticated", "true");
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var providers = await response.Content.ReadFromJsonAsync<List<ProviderResponse>>();
        Assert.Equal(2, providers!.Count);
    }
}
