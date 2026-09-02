using System.Security.Claims;
using System.Text.Encodings.Web;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Core.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataPitcher.Api.IntegrationTests;

public sealed class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public FakeDataPitcherApplication Application { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IDataPitcherApplication>(Application);
            services.AddSingleton<IResourceAccessGrantReader, TestResourceAccessGrantReader>();
            services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        });
    }
}

public sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiBoundaryAuthorizationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Unauthenticated")) return Task.FromResult(AuthenticateResult.NoResult());

        var permissions = Request.Headers.TryGetValue("X-Test-Permissions", out var values)
            ? values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Permissions.All.Select(permission => permission.Value).ToArray();

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-principal") };
        claims.AddRange(permissions.Select(permission => new Claim(ApiClaimTypes.Permission, permission)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class TestResourceAccessGrantReader(IHttpContextAccessor accessor) : IResourceAccessGrantReader
{
    public Task<bool> IsGrantedAsync(ClaimsPrincipal principal, ApiResource resource, CancellationToken cancellationToken)
    {
        var deniedHeader = accessor.HttpContext?.Request.Headers["X-Test-Denied-Resource"].ToString();
        var deniedId = Guid.TryParse(deniedHeader, out var parsed) ? parsed : (Guid?)null;
        var resourceId = resource switch
        {
            ConnectionResource connection => connection.ConnectionId,
            PlanResource plan => plan.PlanId,
            JobResource job => job.JobId,
            _ => (Guid?)null,
        };
        return Task.FromResult(deniedId is null || resourceId != deniedId);
    }
}

public sealed class FakeDataPitcherApplication : IDataPitcherApplication
{
    public List<string> Invocations { get; } = [];
    public CancellationToken? LastCancellationToken { get; private set; }
    public string? LastIdempotencyKey { get; private set; }
    public Func<CancellationToken, Task>? Delay { get; set; }

    public Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ListConnectionsAsync), cancellationToken,
            () => (IReadOnlyList<ConnectionResponse>)[new ConnectionResponse(Guid.NewGuid(), "Source", "sqlserver", "Healthy", "etag-1")]);

    public Task<ConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(CreateConnectionAsync), cancellationToken,
            () => new ConnectionResponse(Guid.NewGuid(), request.DisplayName, request.ProviderId, "Unknown", "etag-1"));

    public Task<OperationReceiptResponse> QueueConnectionCheckAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueConnectionCheckAsync), cancellationToken, () => Receipt(connectionId: connectionId));

    public Task<OperationReceiptResponse> QueueSchemaScanAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueSchemaScanAsync), cancellationToken, () => Receipt(connectionId: connectionId));

    public Task<SchemaSnapshotResponse> GetSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetSnapshotAsync), cancellationToken,
            () => new SchemaSnapshotResponse(connectionId, snapshotId, "hash-1", DateTimeOffset.UnixEpoch));

    public Task<SelectionResponse> SaveSelectionAsync(Guid selectionId, SaveSelectionRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(SaveSelectionAsync), cancellationToken, () => new SelectionResponse(selectionId, 1, "etag-1"));

    public Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(Guid selectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueSelectionEvaluationAsync), cancellationToken, () => Receipt());

    public Task<PlanResponse> SavePlanAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(SavePlanAsync), cancellationToken, () => new PlanResponse(planId, 1, null, "etag-1"));

    public Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueuePlanSealAsync), cancellationToken, () => Receipt(planId: planId));

    public Task<OperationReceiptResponse> StartJobAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken)
    {
        LastIdempotencyKey = idempotencyKey;
        return ObserveAsync(nameof(StartJobAsync), cancellationToken, () => Receipt(planId: planId));
    }

    public Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetJobAsync), cancellationToken, () => new JobResponse(jobId, Guid.NewGuid(), "Running", 10, 100));

    public Task<OperationReceiptResponse> QueueJobCommandAsync(Guid jobId, JobCommand command, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueJobCommandAsync), cancellationToken, () => Receipt(jobId: jobId));

    private static OperationReceiptResponse Receipt(Guid? connectionId = null, Guid? planId = null, Guid? jobId = null) =>
        new(Guid.NewGuid(), "queued", new Uri("https://example.test/api/operations/status"), connectionId, planId, jobId);

    private async Task<T> ObserveAsync<T>(string name, CancellationToken cancellationToken, Func<T> result)
    {
        Invocations.Add(name);
        LastCancellationToken = cancellationToken;
        if (Delay is not null) await Delay(cancellationToken);
        return result();
    }
}
