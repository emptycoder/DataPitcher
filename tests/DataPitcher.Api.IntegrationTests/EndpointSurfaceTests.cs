using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;
using Xunit;

namespace DataPitcher.Api.IntegrationTests;

public sealed class EndpointSurfaceTests(ApiWebApplicationFactory factory) : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ListConnections_ReturnsTypedJsonArray()
    {
        using var response = await _client.GetAsync("/api/connections", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var connections = await response.Content.ReadFromJsonAsync<List<ConnectionResponse>>();
        Assert.Single(connections!, connection => connection.DisplayName == "Source");
    }

    [Fact]
    public async Task CreateConnection_ReturnsTypedConnection()
    {
        var request = new CreateConnectionRequest("Target", "postgresql", Guid.NewGuid(), "*");
        using var response = await _client.PostAsJsonAsync("/api/connections", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>();
        Assert.Equal("Target", connection!.DisplayName);
    }

    [Fact]
    public async Task CreateConnection_WhenIfMatchIsMissing_ReturnsValidationProblemDetails()
    {
        var request = new CreateConnectionRequest("Target", "postgresql", Guid.NewGuid(), "");

        using var response = await _client.PostAsJsonAsync("/api/connections", request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task QueueConnectionCheck_ReturnsAcceptedReceipt()
    {
        var connectionId = Guid.NewGuid();
        using var response = await _client.PostAsync($"/api/connections/{connectionId}/checks", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<OperationReceiptResponse>();
        Assert.Equal(connectionId, receipt!.ConnectionId);
    }

    [Fact]
    public async Task QueueSchemaScan_ReturnsAcceptedReceipt()
    {
        var connectionId = Guid.NewGuid();
        using var response = await _client.PostAsync($"/api/connections/{connectionId}/schema-scans", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_ReadsByConnectionAndSnapshotIdentifiers()
    {
        var connectionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        using var response = await _client.GetAsync($"/api/connections/{connectionId}/snapshots/{snapshotId}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<SchemaSnapshotResponse>();
        Assert.Equal(connectionId, snapshot!.ConnectionId);
        Assert.Equal(snapshotId, snapshot.SnapshotId);
    }

    [Fact]
    public async Task SaveSelection_WhenIfMatchIsPresent_UsesSelectionIdentifier()
    {
        var selectionId = Guid.NewGuid();
        var request = new SaveSelectionRequest("My selection", "{}", "etag-0");
        using var response = await _client.PutAsJsonAsync($"/api/selections/{selectionId}", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var selection = await response.Content.ReadFromJsonAsync<SelectionResponse>();
        Assert.Equal(selectionId, selection!.SelectionId);
    }

    [Fact]
    public async Task SaveSelection_WhenIfMatchIsMissing_ReturnsValidationProblemDetails()
    {
        var request = new SaveSelectionRequest("My selection", "{}", "");
        using var response = await _client.PutAsJsonAsync($"/api/selections/{Guid.NewGuid()}", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task QueueSelectionEvaluation_UsesSelectionIdentifier()
    {
        var selectionId = Guid.NewGuid();
        using var response = await _client.PostAsync($"/api/selections/{selectionId}/evaluations", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_factory.Application.Invocations, name => name == "QueueSelectionEvaluationAsync");
    }

    [Fact]
    public async Task SavePlan_WhenIfMatchIsPresent_UsesPlanIdentifier()
    {
        var planId = Guid.NewGuid();
        var request = new SavePlanRequest("My plan", null, "etag-0");
        using var response = await _client.PutAsJsonAsync($"/api/plans/{planId}", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.Equal(planId, plan!.PlanId);
    }

    [Fact]
    public async Task SavePlan_WhenIfMatchIsMissing_ReturnsValidationProblemDetails()
    {
        var request = new SavePlanRequest("My plan", null, "   ");
        using var response = await _client.PutAsJsonAsync($"/api/plans/{Guid.NewGuid()}", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SavePlan_WithAssociations_PassesThemToTheApplication()
    {
        var planId = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        var sourceConnectionId = Guid.NewGuid();
        var targetConnectionId = Guid.NewGuid();
        var request = new SavePlanRequest("My plan", null, "etag-0", selectionId, sourceConnectionId, targetConnectionId);

        using var response = await _client.PutAsJsonAsync($"/api/plans/{planId}", request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(selectionId, _factory.Application.LastPlanRequest?.SelectionId);
        Assert.Equal(sourceConnectionId, _factory.Application.LastPlanRequest?.SourceConnectionId);
        Assert.Equal(targetConnectionId, _factory.Application.LastPlanRequest?.TargetConnectionId);
    }

    [Fact]
    public async Task SavePlan_WhenSourceConnectionAccessIsDenied_Returns403WithoutSaving()
    {
        var sourceConnectionId = Guid.NewGuid();
        var invocations = _factory.Application.Invocations.Count;
        var request = new SavePlanRequest("My plan", null, "etag-0", Guid.NewGuid(), sourceConnectionId, Guid.NewGuid());
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/api/plans/{Guid.NewGuid()}") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Test-Denied-Resource", sourceConnectionId.ToString());

        using var response = await _client.SendAsync(message, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task SavePlan_WhenTargetConnectionAccessIsDenied_Returns403WithoutSaving()
    {
        var targetConnectionId = Guid.NewGuid();
        var invocations = _factory.Application.Invocations.Count;
        var request = new SavePlanRequest("My plan", null, "etag-0", Guid.NewGuid(), Guid.NewGuid(), targetConnectionId);
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/api/plans/{Guid.NewGuid()}") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Test-Denied-Resource", targetConnectionId.ToString());

        using var response = await _client.SendAsync(message, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(invocations, _factory.Application.Invocations.Count);
    }

    [Fact]
    public async Task QueuePlanSeal_UsesPlanIdentifier()
    {
        var planId = Guid.NewGuid();
        using var response = await _client.PostAsync($"/api/plans/{planId}/seal", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<OperationReceiptResponse>();
        Assert.Equal(planId, receipt!.PlanId);
    }

    [Fact]
    public async Task StartJob_WhenIdempotencyKeyIsPresent_ReturnsAcceptedReceipt()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{Guid.NewGuid()}/jobs");
        request.Headers.Add("Idempotency-Key", "request-01");
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<OperationReceiptResponse>();
        Assert.Equal("queued", receipt!.State);
        Assert.Equal("request-01", _factory.Application.LastIdempotencyKey);
    }

    [Fact]
    public async Task StartJob_WhenIdempotencyKeyIsMissing_ReturnsValidationProblemDetails()
    {
        using var response = await _client.PostAsync($"/api/plans/{Guid.NewGuid()}/jobs", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task StartJob_WhenPlanIsUnsealed_ReturnsConflict()
    {
        _factory.Application.StartJobException = new PlanNotSealedException();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{Guid.NewGuid()}/jobs");
            request.Headers.Add("Idempotency-Key", "request-unsealed");
            using var response = await _client.SendAsync(request, CancellationToken.None);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Plan must be sealed before starting a job.", problem.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            _factory.Application.StartJobException = null;
        }
    }

    [Fact]
    public async Task StartJob_WhenPlanDoesNotExist_ReturnsNotFound()
    {
        _factory.Application.StartJobException = new PlanNotFoundException();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{Guid.NewGuid()}/jobs");
            request.Headers.Add("Idempotency-Key", "request-missing");
            using var response = await _client.SendAsync(request, CancellationToken.None);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Plan was not found.", problem.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            _factory.Application.StartJobException = null;
        }
    }

    [Fact]
    public async Task GetJob_ReturnsTypedJobState()
    {
        var jobId = Guid.NewGuid();
        using var response = await _client.GetAsync($"/api/jobs/{jobId}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<JobResponse>();
        Assert.Equal(jobId, job!.JobId);
    }

    [Theory]
    [InlineData(JobCommand.Pause)]
    [InlineData(JobCommand.Resume)]
    [InlineData(JobCommand.Cancel)]
    public async Task QueueJobCommand_ReturnsAcceptedReceipt(JobCommand command)
    {
        var jobId = Guid.NewGuid();
        using var response = await _client.PostAsJsonAsync($"/api/jobs/{jobId}/commands", new JobCommandRequest(command), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<OperationReceiptResponse>();
        Assert.Equal(jobId, receipt!.JobId);
    }

    [Fact]
    public async Task StartJob_WhenRequestIsCancelled_PropagatesCancellationTokenToApplication()
    {
        var ready = new TaskCompletionSource();
        _factory.Application.Delay = async cancellationToken =>
        {
            ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plans/{Guid.NewGuid()}/jobs");
        request.Headers.Add("Idempotency-Key", "request-02");
        var sendTask = _client.SendAsync(request, cts.Token);
        await ready.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.True(_factory.Application.LastCancellationToken!.Value.IsCancellationRequested);
        _factory.Application.Delay = null;
    }
}
