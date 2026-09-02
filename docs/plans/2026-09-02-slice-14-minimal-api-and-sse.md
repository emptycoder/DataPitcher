# DataPitcher Slice 14: Minimal API and Authenticated Server-Sent Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the completed connection, discovery, selection, plan, and job workflows through a deny-by-default Minimal API with safe errors, generated OpenAPI, and resumable authenticated job-progress streams.

**Architecture:** `DataPitcher.Api` is the HTTP composition root: typed transport records and route groups translate requests into the existing application services without moving business rules from Core, Infrastructure, or the provider projects. Authorization is policy- and resource-based at the HTTP boundary, while the authenticated SSE endpoint reads durable, per-job ordered events and re-authorizes the requested job on every open. A test host supplies deterministic implementations of the existing abstractions and uses in-process SQLite where durable event persistence is required; no container is needed for this slice.

**Tech Stack:** .NET SDK 10.0.400, ASP.NET Core Minimal API and OpenAPI, C# latest, `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, `Microsoft.AspNetCore.OpenApi` 10.0.11, SQLite in process, LINQ to DB 6.4.0, xUnit 2.9.3, Coverlet collector, ReportGenerator, Bash.

---

## File Structure

- `DataPitcher.sln` — adds the API and API integration-test projects.
- `src/DataPitcher.Api/DataPitcher.Api.csproj` — web host referencing Core, Auth.Abstractions, Infrastructure, and provider-neutral application services; it does **not** reference an identity-provider package.
- `src/DataPitcher.Api/Program.cs` — composition root, middleware order, explicit API registration, protected OpenAPI document endpoint, and only the two justified anonymous routes.
- `src/DataPitcher.Api/Contracts/ApiContracts.cs` — typed request, response, operation-receipt, safe resource-identifier, and application-boundary records used by every route.
- `src/DataPitcher.Api/Contracts/IDataPitcherApplication.cs` — cancellable API-facing calls implemented by adapters over the completed connection, schema, selection, plan, and job services.
- `src/DataPitcher.Api/Endpoints/EndpointGroups.cs` — route groups for connections, schema scans and snapshots, selections, plans, jobs, liveness, and non-sensitive provider discovery.
- `src/DataPitcher.Api/Authorization/ApiAuthorization.cs` — permission policy constants, requirements, resource models and handlers, authorization-result Problem Details handling, and anonymous-access justification metadata.
- `src/DataPitcher.Api/Errors/ApiProblems.cs` — architecture error classification, fixed safe error code/status/message mapping, correlation extraction, and exception middleware integration.
- `src/DataPitcher.Api/Events/JobEventStream.cs` — SSE framing, `Last-Event-ID` parsing, expiry-bound stream lifetime, and the job-resource stream endpoint.
- `src/DataPitcher.Infrastructure/Events/JobEventStore.cs` — SQLite transactional append/read/trim implementation of the durable job-event contract.
- `src/DataPitcher.Infrastructure/Events/JobEventContracts.cs` — provider-neutral persisted-event, retention-boundary, and reader/writer contracts used by the worker and API.
- `src/DataPitcher.Infrastructure/Migrations/0003-job-events.sql` — jobs’ monotonic event counter, retained-event boundary, and immutable event rows.
- `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs` — registers the embedded migration in ordered version sequence.
- `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj` — embeds migration 0003.
- `src/DataPitcher.Infrastructure/Worker/JobWorker.cs` — publishes only committed state and batch-progress facts after their corresponding durable work completes.
- `tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj` — in-process web-host test assembly.
- `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs` — deterministic authentication, authorization-resource grants, controllable clock, application fake, and SQLite event-store host setup.
- `tests/DataPitcher.Api.IntegrationTests/EndpointSurfaceTests.cs` — typed route contract, status, cancellation, idempotency-header, and 202-accepted behavior coverage.
- `tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs` — mechanical endpoint-metadata invariant and permission/resource authorization tests.
- `tests/DataPitcher.Api.IntegrationTests/ProblemDetailsTests.cs` — complete error-class status/code mapping, correlation/resources, authorization failure, and response-redaction tests.
- `tests/DataPitcher.Api.IntegrationTests/JobEventStreamTests.cs` — durable resume, expired cursor reload, token-expiry closure, and reconnect authorization coverage.
- `tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs` — generated-document security, protected-operation, Problem Details schema, and example-redaction checks.
- `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs` — asserts Core remains dependency-free and that the API is a leaf composition root.
- `docs/plans/2026-09-02-slice-14-minimal-api-and-sse.md` — this implementation plan.

## Scope and Deferrals

This is an HTTP-boundary slice. It exposes the completed domain and orchestration through an intentionally small surface; it does not duplicate closure, selection SQL generation, sealing, target validation, provider bulk work, or job-state logic in route handlers. `IDataPitcherApplication` is a transport-to-application seam, not a new domain layer: its production registration delegates to the existing completed services, and the integration host substitutes a deterministic fake so API tests remain fast and container-free.

The frontend and all identity-provider packages are deliberately excluded. The API depends on the authentication and normalized-identity abstractions supplied by those slices, never on Entra, OIDC, development-auth, browser, React, or TypeScript packages. The frontend slice must use `fetch` with `Authorization` and `Accept: text/event-stream` for progress, manually send `Last-Event-ID`, retry exactly once after a 401 only after reacquiring credentials, and treat 403 as terminal. Native `EventSource` is not usable because it cannot attach that header. Tokens in a URL, including query strings and fragments, are forbidden.

API boundary tests may register a clearly named test-only authentication scheme to present a principal when proving endpoint authorization and behavior. They do not prove token validation; issuer, audience, tenant, signature, and lifetime validation are covered in the authentication-providers slice using its in-process signed-token issuer against the real registered schemes. The test scheme is never registered outside the API integration host.

Every route handler and every asynchronous application or event call accepts the request `CancellationToken`; queued work deliberately outlives that token in the background worker. Every queued schema check, scan, selection evaluation, plan sealing request, transfer start, and job command returns `202 Accepted` plus an `OperationReceiptResponse`. The receipt contains only an operation identifier, a safe resource identifier, state, and a status URI; it never contains a connection string, credential material, secret-reference content, raw claim, or token. Mutable draft requests carry an `If-Match` ETag and non-repeatable transfer start requires `Idempotency-Key`.

Only `GET /health/live` and `GET /api/providers` may be anonymous. Both receive a non-empty `AnonymousAccessJustificationMetadata`; liveness returns no readiness, dependency, configuration, or identity detail, and provider discovery returns only stable public provider identifiers and display names. The OpenAPI document, every `/api` route, and every SSE open is protected. An authenticated fallback policy is necessary but insufficient: the endpoint metadata test below makes the explicit metadata requirement mechanically enforceable.

Core still depends on nothing, and the existing architecture test must continue to enforce it. This slice may add the API composition root and Infrastructure event storage, but must not add ASP.NET, data access, or provider references to Core or Auth.Abstractions. Warnings remain errors; xUnit analyzer diagnostics are build failures. All newly public members need observable tests in the task that creates them. Run focused tests with the in-memory host; `./scripts/test-all.sh` remains the sole merged 100 percent line, branch, and method coverage gate, and API behavior is not exempt. Cover every handler, mapper branch, SSE cursor branch, authorization result, and error path.

### Task 1: Create the API host, integration host, and shared typed contracts

**Files:**
- Create: `src/DataPitcher.Api/DataPitcher.Api.csproj`, `src/DataPitcher.Api/Program.cs`, `src/DataPitcher.Api/Contracts/ApiContracts.cs`, `src/DataPitcher.Api/Contracts/IDataPitcherApplication.cs`, `tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`, `tests/DataPitcher.Api.IntegrationTests/HostSmokeTests.cs`
- Modify: `DataPitcher.sln`, `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/HostSmokeTests.cs`, `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`

1. - [ ] **Write the failing host and architecture tests with complete public-contract coverage.** The smoke test must create `ApiWebApplicationFactory`, request `/health/live`, assert `200 OK`, and assert the body has exactly `status` equal to `live`. Its companion contract test must construct each request/response record with a GUID, ETag, and opaque credential identifier; serialize each response; and assert that only `connectionId`, `planId`, `jobId`, `snapshotId`, `selectionId`, and `operationId` names appear as resource identifiers. Add `Api_IsCompositionRootAndCoreRemainsDependencyFree` to the existing architecture test: Core has no package or project references, Auth.Abstractions references only Core, and API references Core, Auth.Abstractions, Infrastructure, and no API project references it. The test factory must register an in-memory authenticated test scheme only for API boundary tests; it must not claim to validate bearer tokens, which belongs to the identity-provider slice.

```csharp
public sealed record CreateConnectionRequest(string DisplayName, string ProviderId, Guid CredentialId, string IfMatch);
public sealed record ConnectionResponse(Guid ConnectionId, string DisplayName, string ProviderId, string Health, string ETag);
public sealed record OperationReceiptResponse(Guid OperationId, string State, Uri StatusUri, Guid? ConnectionId, Guid? PlanId, Guid? JobId);
public sealed record ProviderResponse(string ProviderId, string DisplayName);
public sealed record ResourceIdentifiers(Guid? ConnectionId, Guid? SnapshotId, Guid? SelectionId, Guid? PlanId, Guid? JobId);
```

2. - [ ] **Run the new tests and confirm the intended absent-project failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~HostSmokeTests"`. Expected: build fails with `MSB3202` because `src/DataPitcher.Api/DataPitcher.Api.csproj` does not exist. Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~Api_IsCompositionRootAndCoreRemainsDependencyFree"`. Expected: FAIL with `InvalidOperationException: Sequence contains no matching element` because the API project is not in the solution.

3. - [ ] **Implement the minimal host and all shared contracts.** Add an SDK Web project with exact project references to Core, Auth.Abstractions, and Infrastructure plus `Microsoft.AspNetCore.OpenApi` 10.0.11. Add an integration-test project with a project reference to API and exact `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, xUnit 2.9.3, runner 3.1.4, and Coverlet 6.0.4. Add both to `DataPitcher.sln`. `Program.cs` must register Problem Details, authentication, authorization, the application boundary, and endpoint registration in that order; configure exception handling before routing/authentication/authorization; expose `public partial class Program` for `WebApplicationFactory<Program>`.

```csharp
public sealed record SchemaSnapshotResponse(Guid ConnectionId, Guid SnapshotId, string Hash, DateTimeOffset CapturedAtUtc);
public sealed record SaveSelectionRequest(string DisplayName, string QueryJson, string IfMatch);
public sealed record SelectionResponse(Guid SelectionId, long Version, string ETag);
public sealed record SavePlanRequest(string DisplayName, string? OperatorNote, string IfMatch);
public sealed record PlanResponse(Guid PlanId, int Version, string? CanonicalHash, string ETag);
public enum JobCommand { Pause, Resume, Cancel }
public sealed record JobResponse(Guid JobId, Guid PlanId, string State, long RowsTransferred, long BytesTransferred);

public interface IDataPitcherApplication
{
    Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken);
    Task<ConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueConnectionCheckAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueSchemaScanAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<SchemaSnapshotResponse> GetSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);
    Task<SelectionResponse> SaveSelectionAsync(Guid selectionId, SaveSelectionRequest request, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueSelectionEvaluationAsync(Guid selectionId, CancellationToken cancellationToken);
    Task<PlanResponse> SavePlanAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueuePlanSealAsync(Guid planId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> StartJobAsync(Guid planId, string idempotencyKey, CancellationToken cancellationToken);
    Task<JobResponse> GetJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<OperationReceiptResponse> QueueJobCommandAsync(Guid jobId, JobCommand command, CancellationToken cancellationToken);
}
```

`ApiWebApplicationFactory` must replace the production boundary registrations with a test-only deterministic `IDataPitcherApplication`, clock, resource-grant reader, and event store. Its fake records the received cancellation token and idempotency key so later endpoint tests prove propagation rather than merely status codes. Do not place production code in the test project.

4. - [ ] **Run host and boundary checks and confirm they pass.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~HostSmokeTests" && dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~Api_IsCompositionRootAndCoreRemainsDependencyFree"`. Expected: both commands report `Passed` with zero failures; the smoke response is only liveness data and Core remains reference-free.

5. - [ ] **Commit the host foundation.** Run: `git add DataPitcher.sln src/DataPitcher.Api tests/DataPitcher.Api.IntegrationTests tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs && git commit -m "feat: add minimal API host foundation"`.

### Task 2: Map the typed minimal route groups and queued-work surface

**Files:**
- Create: `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`
- Modify: `src/DataPitcher.Api/Contracts/ApiContracts.cs`, `src/DataPitcher.Api/Program.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/EndpointSurfaceTests.cs`

1. - [ ] **Write failing endpoint tests for every route family and handler behavior.** In one test per observable behavior, use the authenticated factory client to prove: connections list and creation return typed JSON; connection check and schema scan return 202; a snapshot is read by both connection and snapshot identifiers; selection save and evaluation use their identifiers; plan save and seal use plan identifiers; job start requires `Idempotency-Key`, returns 202, and job read returns typed state; each pause, resume, and cancel command returns 202. Add one cancellation test per asynchronous route group: cancel the request token in the fake before completing the application task and assert the fake observed that same cancelled token. Test missing `If-Match` and missing `Idempotency-Key` as validation Problem Details, not framework exceptions. Use `Assert.Single(collection, predicate)` and `Assert.DoesNotContain(collection, predicate)`; do not use an `Assert.NotNull` return value.

```csharp
[Fact]
public async Task StartJob_WhenIdempotencyKeyIsPresent_ReturnsAcceptedReceipt()
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/plans/11111111-1111-1111-1111-111111111111/jobs");
    request.Headers.Add("Idempotency-Key", "request-01");
    using var response = await _client.SendAsync(request, CancellationToken.None);
    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var receipt = await response.Content.ReadFromJsonAsync<OperationReceiptResponse>();
    Assert.NotNull(receipt);
    Assert.Equal("queued", receipt!.State);
}
```

2. - [ ] **Run the surface test and confirm the missing mapper is the failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~EndpointSurfaceTests"`. Expected: compilation fails with `CS0103: The name 'EndpointGroups' does not exist in the current context` because no route groups are mapped.

3. - [ ] **Implement exact route groups with typed binding and no domain leakage.** Define the remaining typed records before mapping them: `UpdateConnectionRequest`, `SchemaScanRequest`, `SaveSelectionRequest`, `SavePlanRequest`, `JobCommandRequest`, and the `OperationReceiptResponse` already introduced in Task 1. `EndpointGroups.Map` must create `/api/connections`, `/api/selections`, `/api/plans`, and `/api/jobs` groups; all handlers are `async` and include a final `CancellationToken cancellationToken` parameter. Route exactly these paths: `GET|POST /api/connections`, `POST /api/connections/{connectionId:guid}/checks`, `POST /api/connections/{connectionId:guid}/schema-scans`, `GET /api/connections/{connectionId:guid}/snapshots/{snapshotId:guid}`, `PUT /api/selections/{selectionId:guid}`, `POST /api/selections/{selectionId:guid}/evaluations`, `PUT /api/plans/{planId:guid}`, `POST /api/plans/{planId:guid}/seal`, `POST /api/plans/{planId:guid}/jobs`, `GET /api/jobs/{jobId:guid}`, and `POST /api/jobs/{jobId:guid}/commands`.

```csharp
private static async Task<Results<Accepted<OperationReceiptResponse>, ProblemHttpResult>> StartJobAsync(
    Guid planId, HttpRequest request, IDataPitcherApplication application, CancellationToken cancellationToken)
{
    if (!request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency key is required.");

    var receipt = await application.StartJobAsync(planId, values.ToString(), cancellationToken);
    return TypedResults.Accepted(receipt.StatusUri, receipt);
}
```

Use a route-local helper to require a non-empty `If-Match` before forwarding mutable drafts. Routes return the application-provided receipt location and never wait for worker completion. Requests carry opaque credential IDs only; do not accept a connection string, a secret name, a token, a password field, or raw authorization claims. Update the fake application to return deterministic values for every public contract member and to record every handler invocation, so no public record property or application method is uncovered.

4. - [ ] **Run the route suite and confirm all surface behavior passes.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~EndpointSurfaceTests"`. Expected: all route, request-validation, `202 Accepted`, idempotency, ETag, and cancellation tests pass with zero failures.

5. - [ ] **Commit the typed HTTP surface.** Run: `git add src/DataPitcher.Api/Contracts src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Program.cs tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs tests/DataPitcher.Api.IntegrationTests/EndpointSurfaceTests.cs && git commit -m "feat: expose transfer workflow API routes"`.

### Task 3: Enforce deny-by-default permission and resource authorization

**Files:**
- Create: `src/DataPitcher.Api/Authorization/ApiAuthorization.cs`
- Modify: `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `src/DataPitcher.Api/Program.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs`

1. - [ ] **Write failing policy, resource, fallback, and metadata-safety tests.** Test each named policy constant against its existing `Permissions` value rather than role names. Grant and deny a specific `ConnectionResource`, `PlanResource`, and `JobResource`; prove a grant for one GUID cannot authorize another. Prove missing credentials produce 401 Problem Details, a valid identity without a permission produces 403 Problem Details, and an endpoint with only the authenticated fallback is rejected by the metadata safety test. The safety test must enumerate `EndpointDataSource.Endpoints`, select endpoints whose metadata contains `HttpMethodMetadata`, and fail with route pattern plus display name unless exactly one of authorization metadata or `IAllowAnonymous` metadata is present; an anonymous endpoint also requires non-whitespace `AnonymousAccessJustificationMetadata.Reason`.

```csharp
[Fact]
public void RoutedEndpoints_AreExplicitlyProtectedOrJustifiedAnonymous()
{
    var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not null);

    foreach (var endpoint in endpoints)
    {
        var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count != 0;
        var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var justification = endpoint.Metadata.GetMetadata<AnonymousAccessJustificationMetadata>();
        var valid = authorized ^ anonymous && (!anonymous || !string.IsNullOrWhiteSpace(justification?.Reason));
        var route = endpoint is RouteEndpoint routeEndpoint ? routeEndpoint.RoutePattern.RawText : "(no route pattern)";
        Assert.True(valid, $"{endpoint.DisplayName} ({route}) must have exactly one access mode.");
    }
}
```

2. - [ ] **Run the authorization suite and confirm the absent authorization registration failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~EndpointAuthorizationSafetyNetTests"`. Expected: compilation fails with `CS0246` because `AnonymousAccessJustificationMetadata` and `ApiPolicyNames` do not exist.

3. - [ ] **Implement policies, resource handlers, fallback, and explicit endpoint metadata.** Define `ApiPolicyNames` constants for each existing permission; add a policy for connections read/write, schema read/write, selections read/write/raw SQL, plans read/write/seal, and transfers read/write/start. Each constant is built once from the corresponding `Permission.Value`; no endpoint or handler may test `Role`. Define `ApiResource`, `ConnectionResource`, `PlanResource`, and `JobResource`; define `ResourcePermissionRequirement(Permission Permission)` and an `AuthorizationHandler` that asks the existing normalized-identity/resource-grant abstraction about exactly the supplied resource. The ordinary permission requirement uses the same normalized permission snapshot. Define `IValidatedAccessTokenLifetime` here; the separate authentication package supplies the expiry only after validation, and the API never serializes a claim. Register all handlers and set `AuthorizationOptions.FallbackPolicy` to a policy requiring an authenticated user.

```csharp
using System.Security.Claims;

public sealed record AnonymousAccessJustificationMetadata(string Reason);

public static class ApiPolicyNames
{
    public const string ConnectionsRead = "permission:Connections.Read";
    public const string ConnectionsWrite = "permission:Connections.Write";
    public const string SchemaRead = "permission:Schema.Read";
    public const string SchemaWrite = "permission:Schema.Write";
    public const string SelectionsRead = "permission:Selections.Read";
    public const string SelectionsWrite = "permission:Selections.Write";
    public const string SelectionsRawSql = "permission:Selections.RawSql";
    public const string PlansRead = "permission:Plans.Read";
    public const string PlansWrite = "permission:Plans.Write";
    public const string PlansSeal = "permission:Plans.Seal";
    public const string TransfersRead = "permission:Transfers.Read";
    public const string TransfersWrite = "permission:Transfers.Write";
    public const string TransfersStart = "permission:Transfers.Start";
}

public interface IValidatedAccessTokenLifetime
{
    DateTimeOffset GetExpiryUtc(ClaimsPrincipal principal);
}

public static class AnonymousEndpointConventionBuilderExtensions
{
    public static TBuilder AllowAnonymousWithJustification<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        builder.AllowAnonymous();
        builder.WithMetadata(new AnonymousAccessJustificationMetadata(reason));
        return builder;
    }
}
```

Each `/api` group must call `RequireAuthorization` with its named policy, and routes naming a connection, plan, or job must additionally perform resource authorization with the matching resource before calling `IDataPitcherApplication`. `GET /health/live` and `GET /api/providers` alone call `AllowAnonymousWithJustification` with a specific reason. Provider discovery exposes stable provider IDs and display names only. Protect `/openapi/v1.json` with an explicit read policy as well. Register a custom authorization middleware result handler in this task that writes fixed `unauthenticated` and `authorization_denied` Problem Details; Task 4 reuses its correlation and resource-writing helper for the complete mapper. Do not leave framework-generated empty 401/403 bodies.

4. - [ ] **Run the authorization suite and confirm deny-by-default is mechanically enforced.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~EndpointAuthorizationSafetyNetTests"`. Expected: every routed endpoint passes the exclusive metadata invariant, anonymous access is limited to liveness and provider discovery, cross-resource grants fail, and the intended 401/403 cases pass.

5. - [ ] **Commit the authorization safety net.** Run: `git add src/DataPitcher.Api/Authorization/ApiAuthorization.cs src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Program.cs tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs && git commit -m "feat: protect API endpoints by default"`.

#### Redaction proof task ordering

Task 4 proves runtime redaction in successful responses and Problem Details errors: no response, error message, or error payload carries a password, token, client secret, full connection string, or secret-reference content. The generated OpenAPI document does not exist until Task 6, so Task 6 proves redaction in that document and its examples. A test cannot assert about a document that does not exist yet; splitting by artefact keeps each task's red step observable.

### Task 4: Map every architecture error to safe Problem Details and prove runtime redaction

**Files:**
- Create: `src/DataPitcher.Api/Errors/ApiProblems.cs`
- Modify: `src/DataPitcher.Api/Authorization/ApiAuthorization.cs`, `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `src/DataPitcher.Api/Program.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/ProblemDetailsTests.cs`

1. - [ ] **Write failing complete classification, metadata, and runtime-redaction tests.** Create one representative `ApiFault` for every architecture class: Validation, Unauthenticated, Forbidden, IdentityProviderUnavailable, InvalidToken, TenantRejected, GroupResolutionFailed, AuthenticationConfiguration, Connection, SchemaDrift, UnsupportedProviderFeature, QuerySyntax, QueryTimeout, SourceIntegrity, TargetConflict, TypeConversion, ConstraintCycle, BulkWrite, TransientDatabaseFailure, Cancelled, Verification, and Internal. Assert the mapper returns the specified status and stable lower-case error code for each, contains a fixed human-readable detail, one correlation identifier, and only the relevant typed GUID identifiers. Drive a representative exception through the host and assert `application/problem+json`, not an HTML, empty, or raw-exception response. Test every routed endpoint declares `application/problem+json` metadata for its standard error responses. Configure the fake to throw an exception whose message includes all forbidden sentinel values; assert neither that message nor the sentinel appears in a successful response, Problem Details body, error message, or response headers.

```csharp
public static readonly IReadOnlyDictionary<ApiErrorClass, (int Status, string Code)> Expected =
    new Dictionary<ApiErrorClass, (int Status, string Code)>
    {
        [ApiErrorClass.Validation] = (400, "validation_failed"),
        [ApiErrorClass.Unauthenticated] = (401, "unauthenticated"),
        [ApiErrorClass.Forbidden] = (403, "authorization_denied"),
        [ApiErrorClass.IdentityProviderUnavailable] = (503, "identity_provider_unavailable"),
        [ApiErrorClass.InvalidToken] = (401, "invalid_token"),
        [ApiErrorClass.TenantRejected] = (403, "tenant_rejected"),
        [ApiErrorClass.GroupResolutionFailed] = (503, "authorization_indeterminate"),
        [ApiErrorClass.AuthenticationConfiguration] = (500, "authentication_configuration_error"),
        [ApiErrorClass.Connection] = (502, "connection_failed"),
        [ApiErrorClass.SchemaDrift] = (409, "schema_drift"),
        [ApiErrorClass.UnsupportedProviderFeature] = (422, "unsupported_provider_feature"),
        [ApiErrorClass.QuerySyntax] = (400, "query_syntax_invalid"),
        [ApiErrorClass.QueryTimeout] = (504, "query_timeout"),
        [ApiErrorClass.SourceIntegrity] = (409, "source_integrity_failed"),
        [ApiErrorClass.TargetConflict] = (409, "target_conflict"),
        [ApiErrorClass.TypeConversion] = (422, "type_conversion_failed"),
        [ApiErrorClass.ConstraintCycle] = (422, "constraint_cycle"),
        [ApiErrorClass.BulkWrite] = (502, "bulk_write_failed"),
        [ApiErrorClass.TransientDatabaseFailure] = (503, "transient_database_failure"),
        [ApiErrorClass.Cancelled] = (409, "operation_cancelled"),
        [ApiErrorClass.Verification] = (422, "verification_failed"),
        [ApiErrorClass.Internal] = (500, "internal_error"),
    };
```

2. - [ ] **Run the focused test and confirm the mapper is absent.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ProblemDetailsTests"`. Expected: compilation fails with `CS0246: The type or namespace name 'ApiErrorClass' could not be found`.

3. - [ ] **Implement the closed mapping and exception/auth result writers.** Define `ApiErrorClass`, `ApiFault(ApiErrorClass ErrorClass, ResourceIdentifiers Resources)`, and `ApiProblemMapper.Map`. The mapper must switch exhaustively over the closed enum, generate fixed safe message text from the class rather than `Exception.Message`, set `Status`, `Title`, `Detail`, and `Type`, and add extensions named `code`, `correlationId`, and `resources`. Correlation IDs come from a validated `X-Correlation-ID` or a newly generated GUID; do not echo an arbitrary header value. `ApiExceptionHandler` uses one explicit, ordered type-pattern switch: `OperationCanceledException` is Cancelled only when the request was cancelled; `ArgumentException` is Validation; `InvalidJobStateTransitionException`, `RootConflictException`, and `TargetFenceLostException` are TargetConflict; `BlockedTableException` is UnsupportedProviderFeature; `ManifestSealMismatchException` is SourceIntegrity; and `TransferAttemptException`, `NonResumableInterruptedException`, and `SimulatedWorkerFaultException` are Internal. The default arm maps every unmapped exception to Internal without inspecting its message, inner exception, data, or type name. This deliberate closed mapping must not use convention, string matching, or reflection: an implicit mapping can silently misclassify a newly introduced exception, yielding both a wrong HTTP status and a wrong operator diagnosis, whereas the explicit default fails safely without leaking its message. Where a type is ambiguous, this mapping selects the safest non-retryable interpretation and never reveals resource existence. The custom authorization result handler maps challenge to Unauthenticated and forbid to Forbidden using the same writer. Add `ProducesProblem` metadata for each standard error response to every route in `EndpointGroups.cs` and the two anonymous routes.

```csharp
public enum ApiErrorClass
{
    Validation, Unauthenticated, Forbidden, IdentityProviderUnavailable, InvalidToken, TenantRejected,
    GroupResolutionFailed, AuthenticationConfiguration, Connection, SchemaDrift, UnsupportedProviderFeature,
    QuerySyntax, QueryTimeout, SourceIntegrity, TargetConflict, TypeConversion, ConstraintCycle, BulkWrite,
    TransientDatabaseFailure, Cancelled, Verification, Internal,
}

public sealed record ApiFault(ApiErrorClass ErrorClass, ResourceIdentifiers Resources);

public static class ApiProblemMapper
{
    private sealed record Definition(int Status, string Code, string Title, string Message, string Type);
    private static readonly IReadOnlyDictionary<ApiErrorClass, Definition> Definitions = new Dictionary<ApiErrorClass, Definition>
    {
        [ApiErrorClass.Validation] = new(400, "validation_failed", "Validation failed", "The request is not valid.", "urn:datapitcher:validation"),
        [ApiErrorClass.Unauthenticated] = new(401, "unauthenticated", "Authentication required", "Authentication is required for this operation.", "urn:datapitcher:unauthenticated"),
        [ApiErrorClass.Forbidden] = new(403, "authorization_denied", "Authorization denied", "You are not allowed to perform this operation.", "urn:datapitcher:forbidden"),
        [ApiErrorClass.IdentityProviderUnavailable] = new(503, "identity_provider_unavailable", "Identity provider unavailable", "Authentication cannot be completed now.", "urn:datapitcher:identity-provider-unavailable"),
        [ApiErrorClass.InvalidToken] = new(401, "invalid_token", "Invalid credentials", "The supplied credentials are not valid.", "urn:datapitcher:invalid-token"),
        [ApiErrorClass.TenantRejected] = new(403, "tenant_rejected", "Tenant rejected", "This tenant is not allowed.", "urn:datapitcher:tenant-rejected"),
        [ApiErrorClass.GroupResolutionFailed] = new(503, "authorization_indeterminate", "Authorization unavailable", "Authorization cannot be determined now.", "urn:datapitcher:authorization-indeterminate"),
        [ApiErrorClass.AuthenticationConfiguration] = new(500, "authentication_configuration_error", "Authentication configuration error", "Authentication is unavailable.", "urn:datapitcher:authentication-configuration"),
        [ApiErrorClass.Connection] = new(502, "connection_failed", "Connection failed", "The database connection could not be used.", "urn:datapitcher:connection"),
        [ApiErrorClass.SchemaDrift] = new(409, "schema_drift", "Schema drift", "The schema changed since it was inspected.", "urn:datapitcher:schema-drift"),
        [ApiErrorClass.UnsupportedProviderFeature] = new(422, "unsupported_provider_feature", "Unsupported provider feature", "The selected provider capability is unavailable.", "urn:datapitcher:unsupported-provider-feature"),
        [ApiErrorClass.QuerySyntax] = new(400, "query_syntax_invalid", "Invalid query", "The selection query is not valid.", "urn:datapitcher:query-syntax"),
        [ApiErrorClass.QueryTimeout] = new(504, "query_timeout", "Query timeout", "The database query did not finish in time.", "urn:datapitcher:query-timeout"),
        [ApiErrorClass.SourceIntegrity] = new(409, "source_integrity_failed", "Source integrity failure", "The source data no longer meets the plan requirements.", "urn:datapitcher:source-integrity"),
        [ApiErrorClass.TargetConflict] = new(409, "target_conflict", "Target conflict", "The target conflicts with the requested transfer.", "urn:datapitcher:target-conflict"),
        [ApiErrorClass.TypeConversion] = new(422, "type_conversion_failed", "Type conversion failed", "A required value conversion is unsafe.", "urn:datapitcher:type-conversion"),
        [ApiErrorClass.ConstraintCycle] = new(422, "constraint_cycle", "Constraint cycle", "The planned relationship cycle cannot be transferred safely.", "urn:datapitcher:constraint-cycle"),
        [ApiErrorClass.BulkWrite] = new(502, "bulk_write_failed", "Bulk write failed", "The target write could not be completed.", "urn:datapitcher:bulk-write"),
        [ApiErrorClass.TransientDatabaseFailure] = new(503, "transient_database_failure", "Temporary database failure", "The database is temporarily unavailable.", "urn:datapitcher:transient-database"),
        [ApiErrorClass.Cancelled] = new(409, "operation_cancelled", "Operation cancelled", "The operation was cancelled.", "urn:datapitcher:cancelled"),
        [ApiErrorClass.Verification] = new(422, "verification_failed", "Verification failed", "The transfer did not pass verification.", "urn:datapitcher:verification"),
        [ApiErrorClass.Internal] = new(500, "internal_error", "Internal error", "The operation could not be completed.", "urn:datapitcher:internal"),
    };

    public static ProblemDetails Map(ApiFault fault, string correlationId)
    {
        var definition = Definitions[fault.ErrorClass];
        var problem = new ProblemDetails { Status = definition.Status, Title = definition.Title, Detail = definition.Message, Type = definition.Type };
        problem.Extensions["code"] = definition.Code;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["resources"] = fault.Resources;
        return problem;
    }
}
```

Do not serialize `ClaimsPrincipal`, exception data, stack traces, credentials, connection settings, source/target names that contain credentials, or secret-reference values. Add `ProducesProblem` metadata for each standard error response on every route so generated documentation and runtime output agree.

4. - [ ] **Run the complete error suite and confirm it passes.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ProblemDetailsTests"`. Expected: all 22 representative classes map to the exact asserted status/code pairs; correlation and typed resources appear; authorization failures are Problem Details; all sentinel redaction assertions pass.

5. - [ ] **Commit safe error handling.** Run: `git add src/DataPitcher.Api/Errors/ApiProblems.cs src/DataPitcher.Api/Authorization/ApiAuthorization.cs src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Program.cs tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs tests/DataPitcher.Api.IntegrationTests/ProblemDetailsTests.cs && git commit -m "feat: return safe API problem details"`.

### Task 5: Persist ordered job events and stream them with authenticated SSE

#### Committed-byte contract amendment

`TransferUnit` carries the existing per-batch byte count and `TargetCheckpoint` carries the cumulative committed byte count. `JobWorker` uses the checkpoint value only after the target commit when appending progress events. These additive worker contracts propagate the bounded transfer pipeline's existing byte accounting without introducing a new measurement or changing a provider project.

**Files:**
- Create: `src/DataPitcher.Infrastructure/Events/JobEventContracts.cs`, `src/DataPitcher.Infrastructure/Events/JobEventStore.cs`, `src/DataPitcher.Infrastructure/Migrations/0003-job-events.sql`, `src/DataPitcher.Api/Events/JobEventStream.cs`
- Modify: `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`, `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs`, `src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs`, `src/DataPitcher.Infrastructure/Worker/JobWorker.cs`, `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `src/DataPitcher.Api/Errors/ApiProblems.cs`, `src/DataPitcher.Api/Program.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/JobEventStreamTests.cs`

1. - [ ] **Write failing durable-event and SSE tests.** With the migrated in-process SQLite store, append immutable state and committed-batch facts for one job; reconnect with `Last-Event-ID` equal to the first ID and assert the stream contains only strictly later ordered IDs. Open an empty stream, append one event, signal the stream, and assert it remains open long enough to deliver that frame. Trim the event history, reconnect behind `OldestAvailableEventId - 1`, and assert a `409` Problem Details code `event_cursor_expired` with `reloadRequired` true; assert no attempt to infer a missing event. Open the same job stream twice with different grant decisions and prove the second open is forbidden, recording two resource authorization calls. Verify a challenge returns 401, a resource denial returns 403, no stream URL accepts an access token parameter, and a short-lived authenticated stream closes at expiry without emitting a later event. Test SSE framing exactly: `id`, `event`, JSON `data`, and a blank terminator.

```csharp
[Fact]
public async Task JobEvents_WhenReconnectsAfterAnAuthorizedOpen_ReauthorizesTheSpecificJob()
{
    await _events.AppendAsync(new JobEventAppend(_jobId, "state", new JobEventPayload("running", 0, 0)), CancellationToken.None);
    await _client.GetAsync($"/api/jobs/{_jobId}/events", CancellationToken.None);
    _grants.AllowJob(_jobId, false);
    using var second = await _client.GetAsync($"/api/jobs/{_jobId}/events", CancellationToken.None);
    Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    Assert.Equal(2, _grants.JobAuthorizationCalls);
}
```

2. - [ ] **Run the stream suite and confirm its missing contracts fail.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~JobEventStreamTests"`. Expected: compilation fails with `CS0246` because `JobEventAppend` and `IJobEventReader` do not exist.

3. - [ ] **Implement a durable per-job sequence, retention boundary, publisher, and SSE endpoint.** Add `JobEventStreams(JobId, NextEventId, OldestAvailableEventId)` and `JobEvents(JobId, EventId, EventType, State, RowsTransferred, BytesTransferred, OccurredUtc)` with `(JobId, EventId)` primary key. In one SQLite transaction, append atomically allocates the next positive event ID and inserts the immutable safe payload; it never uses `MAX(EventId) + 1`. `ReadAfterAsync` orders ascending and throws `EventCursorExpiredException` when a supplied cursor is below the persisted boundary. Trimming advances the boundary transactionally. The worker appends a state event after its corresponding state transition and a progress event only after the target batch commit; progress events are advisory and never become correctness checkpoints.

```csharp
public sealed record JobEventPayload(string State, long RowsTransferred, long BytesTransferred);
public sealed record JobEvent(Guid JobId, long EventId, string EventType, JobEventPayload Payload, DateTimeOffset OccurredAtUtc);
public sealed record JobEventAppend(Guid JobId, string EventType, JobEventPayload Payload);
public sealed record JobEventPage(IReadOnlyList<JobEvent> Events, long OldestAvailableEventId);
public interface IJobEventWriter { Task<JobEvent> AppendAsync(JobEventAppend append, CancellationToken cancellationToken); }
public interface IJobEventReader { Task<JobEventPage> ReadAfterAsync(Guid jobId, long? lastEventId, CancellationToken cancellationToken); }
public interface IJobEventSignal
{
    Task WaitAsync(Guid jobId, long lastObservedEventId, CancellationToken cancellationToken);
    void Publish(JobEvent jobEvent);
}
public sealed class EventCursorExpiredException(long oldestAvailableEventId) : InvalidOperationException
{
    public long OldestAvailableEventId { get; } = oldestAvailableEventId;
}
```

Map `GET /api/jobs/{jobId:guid}/events`; accept only the `Last-Event-ID` header, reject malformed IDs as validation, and reject `access_token`, `token`, and `authorization` query keys before reading events. Do not bind any query credential. Before each stream open, call `IAuthorizationService.AuthorizeAsync` with a fresh `JobResource(jobId)` and transfers-read policy. Obtain expiry through Task 3’s `IValidatedAccessTokenLifetime`, build a linked cancellation source ending at that validated expiry, then loop: read durable events strictly after the highest sent ID, frame and flush them, and await `IJobEventSignal.WaitAsync` when no later event is present. `JobEventStore.AppendAsync` persists first and publishes only after commit; after every wake the stream rereads SQLite, so the signal is never the source of truth and repeated signals cannot duplicate IDs. A new HTTP request necessarily repeats both authentication and resource authorization. Extend the existing Problem Details mapper to convert `EventCursorExpiredException` to `409 event_cursor_expired` and add a boolean `reloadRequired` extension. The endpoint uses fixed safe event payloads, not exception text or arbitrary JSON.

4. - [ ] **Run the SSE suite and confirm persistence, resumption, expiry, and reconnect checks pass.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~JobEventStreamTests"`. Expected: resumed streams are strictly ordered and duplicate-free, expired cursors require a full reload, every reconnect authorizes the job again, 401 and 403 remain distinct, and expiry closes the stream.

5. - [ ] **Commit durable progress streaming.** Run: `git add src/DataPitcher.Infrastructure/Events src/DataPitcher.Infrastructure/Migrations/0003-job-events.sql src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj src/DataPitcher.Infrastructure/Worker/JobWorker.cs src/DataPitcher.Api/Events/JobEventStream.cs src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Errors/ApiProblems.cs src/DataPitcher.Api/Program.cs tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs tests/DataPitcher.Api.IntegrationTests/JobEventStreamTests.cs && git commit -m "feat: stream durable authenticated job progress"`.

### Task 6: Generate protected OpenAPI and close the coverage gate

**Files:**
- Create: `tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs`
- Modify: `src/DataPitcher.Api/Program.cs`, `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `src/DataPitcher.Api/Errors/ApiProblems.cs`, `scripts/test-unit.sh`
- Test: `tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs`

1. - [ ] **Write the failing generated-document and redaction tests.** Request the protected `/openapi/v1.json` through the authenticated test client and parse it as JSON. Assert an HTTP bearer security scheme exists; every protected route operation has a matching security requirement; `/health/live` and `/api/providers` alone are anonymous and carry a non-empty anonymous-justification extension; each operation documents `application/problem+json` for its declared error statuses; and the SSE operation advertises `text/event-stream` plus `Last-Event-ID`. Recursively inspect every OpenAPI example, default, enum value, description, and response body collected from representative error calls; assert none contains any forbidden credential sentinel. This task owns generated-document and example redaction because the document is introduced here. Assert a unauthenticated OpenAPI request is a 401 Problem Details response.

```csharp
[Fact]
public async Task OpenApi_DeclaresBearerSecurityAndProblemDetailsWithoutSecrets()
{
    using var response = await _client.GetAsync("/openapi/v1.json", CancellationToken.None);
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    Assert.DoesNotContain(AllStrings(document.RootElement), value => ForbiddenSentinels.Any(sentinel => value.Contains(sentinel, StringComparison.Ordinal)));
}
```

2. - [ ] **Run the OpenAPI test and confirm the missing registration failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~OpenApiTests"`. Expected: FAIL because `/openapi/v1.json` returns `404 NotFound`; `AddOpenApi` and `MapOpenApi` are not yet registered.

3. - [ ] **Configure and annotate generated OpenAPI without a second documentation stack.** Register `AddOpenApi`, add a document transformer that declares the named HTTP bearer scheme, and add an operation transformer that reads endpoint authorization metadata. Protected operations receive a bearer security requirement; justified anonymous operations receive an `x-datapitcher-anonymous-justification` string; neither marker is inferred from a route name. Map the generated endpoint at `/openapi/{documentName}.json`, require its explicit read policy, and preserve the standard Problem Details response schema. Attach `Produces`, `ProducesProblem`, request-body, typed-response, and SSE content metadata directly to the route handlers so the document derives from executable routes. Do not add Swashbuckle, a handwritten parallel JSON contract, example credentials, or an anonymous documentation endpoint.

4. - [ ] **Run focused OpenAPI and full merged coverage verification.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~OpenApiTests" && ./scripts/test-all.sh`. Expected: the focused suite passes the security, Problem Details, SSE, and redaction assertions; the final command exits zero and prints `Merged coverage: line=100% branch=100% method=100%`. If integration tests are not already included by the solution, update `scripts/test-unit.sh` and the solution test inventory before this run; do not exclude API assemblies, handlers, error paths, or authorization failures from collection.

5. - [ ] **Commit OpenAPI verification and the complete slice.** Run: `git add src/DataPitcher.Api/Program.cs src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Errors/ApiProblems.cs tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs scripts/test-unit.sh && git commit -m "feat: publish secured API OpenAPI document"`.

## Self-Review

- [ ] **Coverage:** Tasks 1–6 assign observable tests to every public contract, route, policy constant, resource handler, Problem Details class, error-class branch, event-store branch, stream framing path, token-expiry path, 401/403 result, and OpenAPI transformer. The final command is the existing merged line, branch, and method 100 percent gate; API behavior has no exemption.
- [ ] **Authorization and secret review:** The fallback policy, explicit endpoint metadata invariant, exclusive anonymous justification rule, per-resource checks, reconnect authorization, URL-token prohibition, fixed safe errors, and recursive runtime/OpenAPI redaction test are all covered. Only liveness and non-sensitive provider discovery are anonymous.
- [ ] **Deferrals:** React/fetch implementation, generated TypeScript client consumption, Entra/OIDC/development authentication registration, real issuer/JWKS token-validation tests, and provider-container integration remain in their separate slices. This plan adds no frontend file and no identity-provider package.
- [ ] **Consistency:** Checked that every type used in a later task is introduced in an earlier task or in the same task’s implementation step; route and policy names, `Last-Event-ID`, `OperationReceiptResponse`, resource records, error codes, and method signatures are consistent. C# examples avoid keyword-named pattern variables, target-typed `new()` as a `params` argument, treating `Assert.NotNull` as a value, and analyzer-invalid assertion shapes.
