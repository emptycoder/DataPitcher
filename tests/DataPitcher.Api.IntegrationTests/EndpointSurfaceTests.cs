using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using DataPitcher.Api.Contracts;
using DataPitcher.Api.Endpoints;
using DataPitcher.Core.Authorization;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Storage;
using LinqToDB.Data;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using System.IdentityModel.Tokens.Jwt;
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
    public async Task GetSnapshot_ReturnsTablesAndForeignKeys()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/connections/{Guid.NewGuid()}/snapshots/{Guid.NewGuid()}");
        request.Headers.Add("Authorization", "Bearer " + AccessToken(Permissions.SchemaRead));
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var table = document.RootElement.GetProperty("tables").EnumerateArray().Single();
        var column = table.GetProperty("columns").EnumerateArray().Single();
        var foreignKey = document.RootElement.GetProperty("foreignKeys").EnumerateArray().Single();

        Assert.Equal("Orders", table.GetProperty("name").GetString());
        Assert.Equal("int", column.GetProperty("storeType").GetString());
        Assert.False(column.GetProperty("isNullable").GetBoolean());
        Assert.Equal("PK_Orders", table.GetProperty("primaryKey").GetProperty("name").GetString());
        Assert.Equal("Customers", foreignKey.GetProperty("parentTable").GetProperty("name").GetString());
        Assert.Equal("CustomerId", foreignKey.GetProperty("childColumns").EnumerateArray().Single().GetString());
        Assert.Equal("Id", foreignKey.GetProperty("parentColumns").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task GetSnapshot_WhenSnapshotBelongsToAnotherConnection_ReturnsNotFound()
    {
        var requestedConnectionId = Guid.NewGuid();
        var otherConnectionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        _factory.Application.SnapshotLookup = (connectionId, _) => connectionId == otherConnectionId ? new SchemaSnapshotResponse(otherConnectionId, snapshotId, "other-hash", DateTimeOffset.UnixEpoch) : null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/connections/{requestedConnectionId}/snapshots/{snapshotId}");
            request.Headers.Add("Authorization", "Bearer " + AccessToken(Permissions.SchemaRead));
            using var response = await _client.SendAsync(request, CancellationToken.None);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            _factory.Application.SnapshotLookup = null;
        }
    }

    [Fact]
    public async Task ListSnapshots_ReturnsSnapshotIdentifiers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/connections/{Guid.NewGuid()}/snapshots");
        request.Headers.Add("Authorization", "Bearer " + AccessToken(Permissions.SchemaRead));
        using var response = await _client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.NotEqual(Guid.Empty, document.RootElement.EnumerateArray().Single().GetProperty("snapshotId").GetGuid());
    }

    [Fact]
    public async Task GetSchemaDependencyGraph_UsesThePlanSourceSnapshotInsteadOfAnotherConnectionSnapshot()
    {
        var profiles = _factory.Services.GetRequiredService<ConnectionProfileStore>();
        var source = await profiles.CreateAsync(new ConnectionProfileDraft("Source", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "source"), "app", "__datapitcher"), Guid.NewGuid().ToString("N"), CancellationToken.None);
        var unrelated = await profiles.CreateAsync(new ConnectionProfileDraft("Unrelated", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "unrelated"), "app", "__datapitcher"), Guid.NewGuid().ToString("N"), CancellationToken.None);
        var planId = Guid.NewGuid();
        await _factory.Services.GetRequiredService<PlanStore>().SaveAsync(planId, "Plan", null, "create", CancellationToken.None);
        await _factory.Services.GetRequiredService<PlanStore>().SealAsync(planId, Plan(source.ConnectionId), CancellationToken.None);
        SeedSnapshot(source.ConnectionId, "source-hash", SourceSchema(), "2026-09-01T00:00:00.0000000+00:00");
        SeedSnapshot(unrelated.ConnectionId, "unrelated-hash", UnrelatedSchema(), "2026-09-02T00:00:00.0000000+00:00");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/plans/{planId}/schema-dependency-graph");
        request.Headers.Add("Authorization", "Bearer " + AccessToken(Permissions.PlansRead));
        using var response = await _client.SendAsync(request, CancellationToken.None);
        var graph = await response.Content.ReadFromJsonAsync<PlanSchemaDependencyGraphResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(graph!.Tables, table => table.Name == "Orders");
        Assert.Contains(graph.Relationships, relationship => relationship.Name == "FK_Orders_Customers");
        Assert.DoesNotContain(graph.Tables, table => table.Name == "Secrets");
    }

    [Fact]
    public async Task GetSchemaDependencyGraph_WhenPlanSnapshotIsMissing_ReturnsNotFound()
    {
        var profiles = _factory.Services.GetRequiredService<ConnectionProfileStore>();
        var source = await profiles.CreateAsync(new ConnectionProfileDraft("Source", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "source"), "app", "__datapitcher"), Guid.NewGuid().ToString("N"), CancellationToken.None);
        var planId = Guid.NewGuid();
        await _factory.Services.GetRequiredService<PlanStore>().SaveAsync(planId, "Plan", null, "create", CancellationToken.None);
        await _factory.Services.GetRequiredService<PlanStore>().SealAsync(planId, Plan(source.ConnectionId, "missing-hash"), CancellationToken.None);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/plans/{planId}/schema-dependency-graph");
        request.Headers.Add("Authorization", "Bearer " + AccessToken(Permissions.PlansRead));
        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private void SeedSnapshot(Guid connectionId, string hash, string content, string createdUtc)
    {
        using var database = _factory.Services.GetRequiredService<ControlDatabase>().Open();
        database.Execute("INSERT INTO SchemaSnapshots (SnapshotId, ConnectionId, SnapshotHash, ContentJson, CreatedUtc) VALUES (@snapshotId, @connectionId, @snapshotHash, @contentJson, @createdUtc)", new DataParameter[]
        {
            new("snapshotId", Guid.NewGuid().ToString()),
            new("connectionId", connectionId.ToString()),
            new("snapshotHash", hash),
            new("contentJson", content),
            new("createdUtc", createdUtc),
        });
    }

    private static TransferPlanContent Plan(Guid sourceConnectionId, string sourceSchemaHash = "source-hash") => new(
        new ConnectionFingerprint("postgresql", "source", "source", sourceConnectionId),
        new ConnectionFingerprint("postgresql", "target", "target"),
        new SchemaSnapshotReference(sourceSchemaHash), new SchemaSnapshotReference("target-hash"), [], [], [],
        ConsistencyMode.FrozenKeys, TransferMode.DirectFast, TriggerStrategy.Fire, ConstraintStrategy.Enforce, [], [],
        new BatchTarget(1, 1), VerificationStrategy.Standard, new ManifestCounts(0, 0, 0, 0));

    private static string SourceSchema() => JsonSerializer.Serialize(new
    {
        Tables = new[]
        {
            new { Schema = "app", Name = "Customers", Columns = Array.Empty<object>(), PrimaryKey = (object?)null, UniqueConstraints = Array.Empty<object>() },
            new { Schema = "app", Name = "Orders", Columns = Array.Empty<object>(), PrimaryKey = (object?)null, UniqueConstraints = Array.Empty<object>() },
        },
        ForeignKeys = new[] { new { Name = "FK_Orders_Customers", ChildTable = new { Schema = "app", Name = "Orders" }, ParentTable = new { Schema = "app", Name = "Customers" }, ChildColumns = new[] { "CustomerId" }, ParentColumns = new[] { "Id" }, IsEnforced = true, IsTrusted = true } },
        DatabaseIdentity = "source",
        ProviderVersion = "1",
    });

    private static string UnrelatedSchema() => JsonSerializer.Serialize(new
    {
        Tables = new[] { new { Schema = "app", Name = "Secrets", Columns = Array.Empty<object>(), PrimaryKey = (object?)null, UniqueConstraints = Array.Empty<object>() } },
        ForeignKeys = Array.Empty<object>(),
        DatabaseIdentity = "unrelated",
        ProviderVersion = "1",
    });

    private static string AccessToken(Permission permission) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "https://localhost/datapitcher-dev", "datapitcher-api", [new Claim("sub", "test"), new Claim("permission", permission.Value)],
        expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("api-integration-test-signing-key-32")), SecurityAlgorithms.HmacSha256)));

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
