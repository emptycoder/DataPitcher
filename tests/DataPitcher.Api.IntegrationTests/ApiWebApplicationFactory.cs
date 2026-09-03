using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DataPitcher.Api.Authorization;
using DataPitcher.Api.Contracts;
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Core.Authorization;
using DataPitcher.Infrastructure.Events;
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
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
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"datapitcher-api-{Guid.NewGuid():N}.db");
    private readonly ControlDatabase _database;
    public FakeDataPitcherApplication Application { get; } = new();
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
    public JobEventStore Events { get; }
    public IJobEventSignal EventSignal { get; }
    public TestResourceAccessGrantReader Grants => Services.GetRequiredService<TestResourceAccessGrantReader>();

    public ApiWebApplicationFactory() : this(new JobEventSignal()) { }

    internal ApiWebApplicationFactory(IJobEventSignal eventSignal)
    {
        EventSignal = eventSignal;
        _database = new ControlDatabase($"Data Source={_databasePath}");
        new ControlDatabaseMigrator(_database, Clock).Apply();
        Events = new(_database, Clock, EventSignal);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ControlDatabase:Path", _databasePath);
        builder.UseSetting("Secrets:Root", Path.GetTempPath());
        builder.UseSetting("Authentication:Development:SigningKey", "api-integration-test-signing-key-32");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_database);
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IDataPitcherApplication>(Application);
            services.AddSingleton<TestResourceAccessGrantReader>();
            services.AddSingleton<IResourceAccessGrantReader>(serviceProvider => serviceProvider.GetRequiredService<TestResourceAccessGrantReader>());
            services.AddSingleton<IPermissionDecisionResolver, TestPermissionDecisionResolver>();
            services.AddSingleton<IValidatedAccessTokenLifetime, TestValidatedAccessTokenLifetime>();
            services.AddSingleton<IJobEventWriter>(Events);
            services.AddSingleton<IJobEventReader>(Events);
            services.AddSingleton(EventSignal);
            services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}

public sealed class TestClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;
    public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
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
        if (Request.Headers.TryGetValue("X-Test-Raw-Claim", out var rawClaim))
            claims.Add(new Claim("test-raw-claim", rawClaim.ToString()));
        if (Request.Headers.TryGetValue("X-Test-Token-Expiry", out var expiry))
            claims.Add(new Claim(TestValidatedAccessTokenLifetime.ExpiryClaim, expiry.ToString()));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class TestResourceAccessGrantReader(IHttpContextAccessor accessor) : IResourceAccessGrantReader
{
    private readonly ConcurrentDictionary<Guid, bool> _jobGrants = [];
    private int _jobAuthorizationCalls;

    public int JobAuthorizationCalls => _jobAuthorizationCalls;
    public void AllowJob(Guid jobId, bool allowed) => _jobGrants[jobId] = allowed;

    public Task<bool> IsGrantedAsync(ClaimsPrincipal principal, ApiResource resource, CancellationToken cancellationToken)
    {
        if (resource is JobResource job)
        {
            Interlocked.Increment(ref _jobAuthorizationCalls);
            if (_jobGrants.TryGetValue(job.JobId, out var allowed)) return Task.FromResult(allowed);
        }
        var deniedHeader = accessor.HttpContext?.Request.Headers["X-Test-Denied-Resource"].ToString();
        var deniedId = Guid.TryParse(deniedHeader, out var parsed) ? parsed : (Guid?)null;
        var resourceId = resource switch
        {
            ConnectionResource connection => connection.ConnectionId,
            PlanResource plan => plan.PlanId,
            JobResource jobResource => jobResource.JobId,
            _ => (Guid?)null,
        };
        return Task.FromResult(deniedId is null || resourceId != deniedId);
    }
}

/// <summary>
/// Reproduces the pre-fix "trust the permission claim" contract exactly, but only inside the test host: the
/// production <see cref="ClaimsPermissionDecisionResolver"/> never reads this claim, so <see cref="TestAuthenticationHandler"/>
/// keeps letting individual tests grant an arbitrary exact permission set via the X-Test-Permissions header
/// without every test having to model role bundles.
/// </summary>
public sealed class TestPermissionDecisionResolver : IPermissionDecisionResolver
{
    public AuthorizationDecision Resolve(ClaimsPrincipal principal, Permission permission) =>
        new(principal.HasClaim(ApiClaimTypes.Permission, permission.Value) ? AuthorizationOutcome.Granted : AuthorizationOutcome.Denied, PermissionSet.Empty);
}

public sealed class TestValidatedAccessTokenLifetime : IValidatedAccessTokenLifetime
{
    public const string ExpiryClaim = "test-expiry";

    public DateTimeOffset GetExpiryUtc(ClaimsPrincipal principal) =>
        DateTimeOffset.TryParse(principal.FindFirst(ExpiryClaim)?.Value, out var expiry) ? expiry : DateTimeOffset.UtcNow.AddMinutes(5);
}

public sealed class FakeDataPitcherApplication : IDataPitcherApplication
{
    public List<string> Invocations { get; } = [];
    public CancellationToken? LastCancellationToken { get; private set; }
    public string? LastIdempotencyKey { get; private set; }
    public SavePlanRequest? LastPlanRequest { get; private set; }
    public Exception? StartJobException { get; set; }
    public Func<CancellationToken, Task>? Delay { get; set; }
    public Func<Guid, Guid, SchemaSnapshotResponse?>? SnapshotLookup { get; set; }
    public OperationStatusResponse? OperationStatus { get; set; }
    public IReadOnlyList<JobSummaryResponse>? JobSummaries { get; set; }

    public Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ListConnectionsAsync), cancellationToken,
            () => (IReadOnlyList<ConnectionResponse>)[new ConnectionResponse(Guid.NewGuid(), "Source", "sqlserver", "Healthy", "etag-1")]);

    public Task<ConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(CreateConnectionAsync), cancellationToken,
            () => new ConnectionResponse(Guid.NewGuid(), request.DisplayName, request.ProviderId, "Unknown", "etag-1"));

    public Task<OperationReceiptResponse> QueueConnectionCheckAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueConnectionCheckAsync), cancellationToken, () => Receipt(connectionId: connectionId));

    public Task<OperationReceiptResponse> QueueSchemaScanAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueSchemaScanAsync), cancellationToken, () => Receipt(connectionId: connectionId, state: "queued"));

    public Task<OperationStatusResponse?> GetOperationStatusAsync(Guid operationId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetOperationStatusAsync), cancellationToken, () => OperationStatus is null ? null : OperationStatus with { OperationId = operationId });

    public Task<IReadOnlyList<SchemaSnapshotSummaryResponse>> ListSnapshotsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ListSnapshotsAsync), cancellationToken,
            () => (IReadOnlyList<SchemaSnapshotSummaryResponse>)[new SchemaSnapshotSummaryResponse(Guid.NewGuid(), "hash-1", DateTimeOffset.UnixEpoch)]);

    public Task<SchemaSnapshotResponse> GetSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetSnapshotAsync), cancellationToken,
            () => Snapshot(connectionId, snapshotId));

    public Task<SchemaSnapshotResponse?> FindSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(FindSnapshotAsync), cancellationToken,
            () => SnapshotLookup is null ? Snapshot(connectionId, snapshotId) : SnapshotLookup(connectionId, snapshotId));

    public Task<SelectionResponse> SaveSelectionAsync(Guid selectionId, SaveSelectionRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(SaveSelectionAsync), cancellationToken, () => new SelectionResponse(selectionId, 1, "etag-1"));

    public Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(Guid selectionId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueSelectionEvaluationAsync), cancellationToken, () => Receipt());

    public Task<PlanResponse> SavePlanAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken)
    {
        LastPlanRequest = request;
        return ObserveAsync(nameof(SavePlanAsync), cancellationToken, () => new PlanResponse(planId, 1, null, "etag-1"));
    }

    public Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueuePlanSealAsync), cancellationToken, () => Receipt(planId: planId));

    public Task<PlanReviewResponse> GetPlanReviewAsync(Guid planId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetPlanReviewAsync), cancellationToken, () => new PlanReviewResponse(planId, 4, new string('A', 64), new("sealed", []), new(12, 9, 7, 2, 4096), [new("permission", true, "Transfer permission is current."), new("sourceHealthy", true, "Source is server-verified Healthy."), new("targetHealthy", true, "Target is server-verified Healthy."), new("schemaValid", true, "Target schema validation passed."), new("noBlockers", true, "No blockers remain."), new("safeMappings", true, "All type mappings are safe."), new("cycleSupported", true, "Cycle strategy is supported."), new("authenticated", true, "Authentication is valid.")], [new(new("sales", "Orders"), new("sales", "Orders"), "Root", 2, 9, 9, 7, 2, 3072, [new("Id", "Id")])], [new("sales.Orders", "FailOnConflict", "Existing target keys fail the plan.")], [new(["sales.Orders", "sales.OrderLines"], "DeferredConstraints", "Constraints are deferred for this component.")], [new("target-satisfied-values", "Target-satisfied dependencies are not refreshed.")], []));

    public Task<InclusionPathResponse> GetPlanInclusionPathAsync(Guid planId, InclusionPathRequest request, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetPlanInclusionPathAsync), cancellationToken, () => new InclusionPathResponse(request.Table, request.StableKey, "Open orders", [new("Root selection", request.Table, request.Table, "Selected as a root row.")]));

    public Task<OperationReceiptResponse> StartJobAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken)
    {
        LastIdempotencyKey = idempotencyKey;
        return ObserveAsync(nameof(StartJobAsync), cancellationToken, () => StartJobException is null ? Receipt(planId: planId, state: "queued") : throw StartJobException);
    }

    public Task<IReadOnlyList<JobSummaryResponse>> ListJobsAsync(CancellationToken cancellationToken) =>
        ObserveAsync(nameof(ListJobsAsync), cancellationToken, () => JobSummaries ?? [new JobSummaryResponse(Guid.NewGuid(), Guid.NewGuid(), "Running", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, 100)]);

    public Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(GetJobAsync), cancellationToken, () => new JobResponse(jobId, Guid.NewGuid(), "Running", 10, 100));

    public Task<OperationReceiptResponse> QueueJobCommandAsync(Guid jobId, JobCommand command, CancellationToken cancellationToken) =>
        ObserveAsync(nameof(QueueJobCommandAsync), cancellationToken, () => Receipt(jobId: jobId));

    private static OperationReceiptResponse Receipt(Guid? operationId = null, Guid? connectionId = null, Guid? planId = null, Guid? jobId = null, string state = "unknown")
    {
        var id = operationId ?? Guid.NewGuid();
        return new(id, state, new Uri("https://example.test/api/operations/" + id), connectionId, planId, jobId);
    }

    private static SchemaSnapshotResponse Snapshot(Guid connectionId, Guid snapshotId) => new(connectionId, snapshotId, "hash-1", DateTimeOffset.UnixEpoch)
    {
        Tables = [new SchemaSnapshotTableResponse("app", "Orders", [new SchemaSnapshotColumnResponse("CustomerId", "int", false)], new SchemaSnapshotKeyResponse("PK_Orders", ["CustomerId"]))],
        ForeignKeys = [new SchemaSnapshotForeignKeyResponse("FK_Orders_Customers", new("app", "Orders"), new("app", "Customers"), ["CustomerId"], ["Id"], true, true)],
    };

    private async Task<T> ObserveAsync<T>(string name, CancellationToken cancellationToken, Func<T> result)
    {
        Invocations.Add(name);
        LastCancellationToken = cancellationToken;
        if (Delay is not null) await Delay(cancellationToken);
        return result();
    }
}
