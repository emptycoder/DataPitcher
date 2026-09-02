# DataPitcher Slice 17: Connections, Schema Scans and Their API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add secure connection profiles, server-derived health checks, immutable schema scans, and their protected HTTP API without exposing a secret or adding a frontend.

**Architecture:** Core defines connection, capability, and canonical snapshot semantics without provider or HTTP dependencies. Infrastructure persists only connection metadata and secret references in SQLite, resolves a reference only at the provider call boundary, and owns health rechecking plus durable schema-scan orchestration. The API remains the composition root: it registers the two provider implementations, translates safe transport records, and enforces policy plus connection-resource authorization before every resource operation.

**Tech Stack:** .NET SDK 10.0.400, C# latest, ASP.NET Core Minimal API/OpenAPI, SQLite in process, LINQ to DB 6.4.0, Npgsql 10.0.3, Microsoft.Data.SqlClient 7.0.2, xUnit 2.9.3, Testcontainers PostgreSQL/MsSql 4.14.0, Coverlet, ReportGenerator, Bash.

---

## File Structure

- `src/DataPitcher.Core/Connections/ConnectionModels.cs` — secret-reference metadata, connection roles, health states, capability evidence, and server-derived assessment rules.
- `src/DataPitcher.Core/Connections/ProviderContracts.cs` — provider-neutral probe, schema-introspection, and provider-registry contracts.
- `src/DataPitcher.Core/Schema/SchemaSnapshotModels.cs` — immutable transfer-relevant schema snapshot, graph projection, and canonical SHA-256 hash.
- `src/DataPitcher.Infrastructure/Connections/SecretReferenceResolver.cs` — environment-variable and mounted-file resolution with no persistence or logging of resolved content.
- `src/DataPitcher.Infrastructure/Connections/ConnectionProfileStore.cs` — SQLite profile CRUD, version ETags, health persistence, and scan-command idempotency.
- `src/DataPitcher.Infrastructure/Connections/ConnectionHealthService.cs` — real probe orchestration and immediate pre-transfer revalidation.
- `src/DataPitcher.Infrastructure/Schema/SchemaSnapshotStore.cs` — immutable snapshot and scan-status persistence.
- `src/DataPitcher.Infrastructure/Schema/SchemaScanWorker.cs` — queued scan execution and snapshot capture.
- `src/DataPitcher.Infrastructure/Migrations/0004-connections-and-schema.sql` — control-database tables for profiles, health, scans, and snapshots.
- `src/DataPitcher.Providers.PostgreSql/PostgreSqlConnectionProbe.cs` and `PostgreSqlSchemaIntrospector.cs` — PostgreSQL capability probe and adapter over the existing three-query catalog reader.
- `src/DataPitcher.Providers.SqlServer/SqlServerConnectionProbe.cs` and `SqlServerSchemaIntrospector.cs` — SQL Server capability probe and adapter over the existing three-query catalog reader.
- `src/DataPitcher.Api/Contracts/ApiContracts.cs` and `IDataPitcherApplication.cs` — safe connection, health, scan, and snapshot transport/application contracts.
- `src/DataPitcher.Api/Contracts/ConnectionSchemaApplication.cs` — production adapter from API contracts to Infrastructure services.
- `src/DataPitcher.Api/Endpoints/EndpointGroups.cs` — explicit protected routes and Problem Details metadata.
- `src/DataPitcher.Api/Program.cs` and `DataPitcher.Api.csproj` — composition-root registrations for provider implementations and the production adapter.
- `tests/DataPitcher.UnitTests/Connections/ConnectionModelsTests.cs` — Core health, requirement, immutability, and canonical-hash coverage.
- `tests/DataPitcher.UnitTests/Infrastructure/ConnectionProfileStoreTests.cs`, `SecretReferenceResolverTests.cs`, and `SchemaScanWorkerTests.cs` — in-process SQLite orchestration and redaction/telemetry evidence.
- `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlConnectionProbeTests.cs` and `PostgreSqlCatalogReaderTests.cs` — real PostgreSQL probe behavior and bounded query-count proof.
- `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerConnectionProbeTests.cs` and `SqlServerCatalogReaderTests.cs` — real SQL Server probe behavior and bounded query-count proof.
- `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`, `ConnectionSchemaEndpointTests.cs`, `ConnectionSchemaAuthorizationTests.cs`, and `SecretLeakageTests.cs` — in-memory host, endpoint, authorization, OpenAPI, and sentinel-leak tests.

## Scope and Deferrals

Profiles store a `SecretReference`, never a password, token, client secret, complete connection string, or resolved reference content. Environment references name a process variable; file references name a file beneath the configured secrets-mount root. Responses reveal only that credential material is reference-backed, never its locator or resolved value. Resolved values may exist only in local variables while a provider opens a connection; they must not be serialized, included in exception text, structured log state, activity tags, audit records, OpenAPI examples, or response headers.

Health is a server fact, not a client assertion. A profile starts `Unknown`, moves to `Checking` while a probe runs, and is then `Healthy`, `Degraded`, or `Unhealthy` for the exact transfer mode and source/target role tested. `Healthy` means every required capability is present; `Degraded` means all required capabilities are present but one or more optional optimization or reduced-resume capabilities are missing; `Unhealthy` means connection, permission, cleanup, identity, or a required capability prevents safe use. A transfer must call the same server-side revalidator immediately after loading its run and before it opens a source or target work session. A posted `healthy` field is not accepted anywhere.

The initial scan reads base tables, columns, stable keys, foreign keys, nullability, CLR/store types, and enforce/trust metadata only. It does not ask for exact row counts, table samples, business rows, statistics, layout coordinates, target compatibility, or schema migration. The schema hash excludes profile ID, profile display name, secret-reference data, captured time, server identity, provider version, health, scan ID, and all row-count information; it includes only canonicalized transfer-relevant metadata. Graph coordinates and frontend work are deliberately deferred. Existing selection, plan, and transfer APIs remain unchanged except for the worker’s mandatory server revalidation seam.

Every new public member is exercised in the task that introduces it. Use `StringComparer.Ordinal` or explicit `StringComparison.Ordinal` for every string identity, sort, comparison, assertion, and hash ordering. Keep tests in process unless an actual database protocol, permission, or catalog query is being proved. Do not assign the void return from xUnit 2.9.3 `Assert.NotNull`, do not use a C# keyword as a pattern variable, and do not pass target-typed `new()` as the sole `params` argument. Use `Assert.Single(collection, predicate)` and `Assert.DoesNotContain(collection, predicate)` where applicable.

### Task 1: Define provider-neutral connection, capability, and immutable schema contracts

**Files:**
- Create: `src/DataPitcher.Core/Connections/ConnectionModels.cs`, `src/DataPitcher.Core/Connections/ProviderContracts.cs`, `src/DataPitcher.Core/Schema/SchemaSnapshotModels.cs`, `tests/DataPitcher.UnitTests/Connections/ConnectionModelsTests.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Connections/ConnectionModelsTests.cs`

1. - [ ] **Step 1: Write the failing complete Core-contract tests.** Construct both legal secret-reference kinds; reject blank locators and a non-absolute mounted-file locator; prove copied capability collections and snapshot collections cannot be mutated; cover all three classifier outcomes plus the `Unknown` and `Checking` persisted states; prove required capability absence and staging cleanup failure are `Unhealthy`; prove optional durable-resume absence is `Degraded`; prove the same metadata in different input order has the same hash; and prove changing a transfer-relevant column, key order, FK enforcement flag, or FK column order changes it. Include the following test body and separate facts for each remaining public constructor/property described above.

```csharp
[Fact]
public void ConnectionHealthClassifier_WhenOnlyOptionalCapabilityIsMissing_IsDegraded()
{
    var requirements = new ConnectionRequirements(
        new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema },
        new HashSet<ConnectionCapability> { ConnectionCapability.SupportsDurableResume });
    var assessment = ConnectionHealthClassifier.Classify(
        requirements,
        new ConnectionProbeEvidence("identity", "version",
            new HashSet<ConnectionCapability> { ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema }, null));

    Assert.Equal(ConnectionHealthState.Degraded, assessment.State);
    Assert.Single(assessment.MissingOptional, capability => capability == ConnectionCapability.SupportsDurableResume);
}
```

2. - [ ] **Step 2: Run the Core tests and confirm the intended missing-namespace failure.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ConnectionModelsTests"`. Expected: compilation fails with `CS0234` because `DataPitcher.Core.Connections` does not exist.

3. - [ ] **Step 3: Implement the complete immutable Core contract and canonical hasher.** Add the following types before Infrastructure or provider code references them. `SecretReference` is metadata only; its locator is intentionally absent from `ConnectionProfileSummary`. `ConnectionRequirements.For` maps source and target requirements for each existing `TransferMode`: base safe capabilities are required, `CanUseServerSideTransfer` is required only for `ServerSide`, and durable staging/resume capabilities are optional for `ResumableStaged`, yielding an honest degraded result rather than a false durability claim.

```csharp
using System.Collections.Frozen;
using DataPitcher.Core.Plans;

namespace DataPitcher.Core.Connections;

public enum SecretReferenceKind { EnvironmentVariable, FileMounted }
public enum ConnectionRole { Source, Target }
public enum ConnectionHealthState { Unknown, Checking, Healthy, Degraded, Unhealthy }
public enum ConnectionCapability
{
    CanConnect, CanReadSchema, CanReadBusinessRows, CanCreateSourceStaging,
    CanDropSourceStaging, CanCreateTargetStaging, CanDropTargetStaging,
    CanBulkInsert, CanPreserveIdentity, CanUseTransactions, CanUseSnapshotIsolation,
    CanDeferConstraints, CanDisableConstraints, CanRevalidateConstraints,
    CanFireTriggers, CanSuppressTriggers, CanReseedGeneratedKeys,
    CanUseServerSideTransfer, SupportsDurableResume,
}

public sealed record SecretReference
{
    public SecretReference(SecretReferenceKind kind, string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        if (kind is SecretReferenceKind.FileMounted && !Path.IsPathFullyQualified(locator))
            throw new ArgumentException("Mounted secret references must be absolute.", nameof(locator));
        Kind = kind;
        Locator = locator;
    }

    public SecretReferenceKind Kind { get; }
    public string Locator { get; }
}

public sealed record ConnectionProfile(
    Guid ConnectionId, string DisplayName, string ProviderId, SecretReference SecretReference,
    string BusinessSchema, string StagingSchema, long Version);
public sealed record ConnectionProfileSummary(
    Guid ConnectionId, string DisplayName, string ProviderId, SecretReferenceKind SecretReferenceKind,
    ConnectionHealthState Health, string ETag);
public sealed class ConnectionRequirements
{
    public ConnectionRequirements(IEnumerable<ConnectionCapability> required, IEnumerable<ConnectionCapability> optional)
    {
        Required = required.ToFrozenSet();
        Optional = optional.ToFrozenSet();
    }

    public IReadOnlySet<ConnectionCapability> Required { get; }
    public IReadOnlySet<ConnectionCapability> Optional { get; }
    public static ConnectionRequirements For(TransferMode mode, ConnectionRole role)
    {
        var required = new HashSet<ConnectionCapability>
        {
            ConnectionCapability.CanConnect, ConnectionCapability.CanReadSchema,
            ConnectionCapability.CanReadBusinessRows, ConnectionCapability.CanUseTransactions,
        };
        var optional = new HashSet<ConnectionCapability> { ConnectionCapability.CanUseSnapshotIsolation };
        if (role is ConnectionRole.Target)
        {
            required.Add(ConnectionCapability.CanBulkInsert);
            required.Add(ConnectionCapability.CanPreserveIdentity);
        }
        if (mode is TransferMode.ServerSide)
            required.Add(ConnectionCapability.CanUseServerSideTransfer);
        if (mode is TransferMode.ResumableStaged && role is ConnectionRole.Source)
        {
            optional.Add(ConnectionCapability.CanCreateSourceStaging);
            optional.Add(ConnectionCapability.CanDropSourceStaging);
            optional.Add(ConnectionCapability.SupportsDurableResume);
        }
        if (mode is TransferMode.ResumableStaged && role is ConnectionRole.Target)
        {
            required.Add(ConnectionCapability.CanCreateTargetStaging);
            required.Add(ConnectionCapability.CanDropTargetStaging);
        }
        return new ConnectionRequirements(required, optional);
    }
}

public sealed class ConnectionProbeEvidence
{
    public ConnectionProbeEvidence(string databaseIdentity, string providerVersion,
        IEnumerable<ConnectionCapability> available, string? cleanupFailureCode)
    {
        DatabaseIdentity = databaseIdentity;
        ProviderVersion = providerVersion;
        Available = available.ToFrozenSet();
        CleanupFailureCode = cleanupFailureCode;
    }

    public string DatabaseIdentity { get; }
    public string ProviderVersion { get; }
    public IReadOnlySet<ConnectionCapability> Available { get; }
    public string? CleanupFailureCode { get; }
}

public sealed class ConnectionAssessment
{
    public ConnectionAssessment(ConnectionHealthState state, string databaseIdentity, string providerVersion,
        IEnumerable<ConnectionCapability> available, IEnumerable<ConnectionCapability> missingRequired,
        IEnumerable<ConnectionCapability> missingOptional, string? cleanupFailureCode)
    {
        State = state;
        DatabaseIdentity = databaseIdentity;
        ProviderVersion = providerVersion;
        Available = available.ToFrozenSet();
        MissingRequired = missingRequired.ToFrozenSet();
        MissingOptional = missingOptional.ToFrozenSet();
        CleanupFailureCode = cleanupFailureCode;
    }

    public ConnectionHealthState State { get; }
    public string DatabaseIdentity { get; }
    public string ProviderVersion { get; }
    public IReadOnlySet<ConnectionCapability> Available { get; }
    public IReadOnlySet<ConnectionCapability> MissingRequired { get; }
    public IReadOnlySet<ConnectionCapability> MissingOptional { get; }
    public string? CleanupFailureCode { get; }
}

public static class ConnectionHealthClassifier
{
    public static ConnectionAssessment Classify(ConnectionRequirements requirements, ConnectionProbeEvidence evidence)
    {
        var required = new HashSet<ConnectionCapability>(requirements.Required);
        var optional = new HashSet<ConnectionCapability>(requirements.Optional);
        var missingRequired = required.Where(capability => !evidence.Available.Contains(capability)).ToHashSet();
        var missingOptional = optional.Where(capability => !evidence.Available.Contains(capability)).ToHashSet();
        var state = evidence.CleanupFailureCode is not null || missingRequired.Count != 0
            ? ConnectionHealthState.Unhealthy
            : missingOptional.Count != 0 ? ConnectionHealthState.Degraded : ConnectionHealthState.Healthy;
        return new(state, evidence.DatabaseIdentity, evidence.ProviderVersion,
            new HashSet<ConnectionCapability>(evidence.Available), missingRequired, missingOptional,
            evidence.CleanupFailureCode);
    }
}
```

`ConnectionRequirements.For` must add target-only `CanBulkInsert`, `CanPreserveIdentity`, and target staging capabilities; source `ResumableStaged` requirements keep durable staging/resume optional so a safely reduced-resume path is `Degraded`, while `ServerSide` requires `CanUseServerSideTransfer`. `ProviderContracts.cs` defines `ConnectionProbeRequest(ConnectionProfile Profile, ConnectionRole Role, TransferMode Mode, string ResolvedConnectionString)`, `ICapabilityDetector.ProbeAsync`, `ISchemaIntrospector.ReadAsync`, and `IConnectionProvider` with `ProviderId`, `CapabilityDetector`, and `SchemaIntrospector`. It rejects an unsupported provider with the fixed code `unsupported_provider`, not an identifier copied from the request. `SchemaSnapshotModels.cs` defines defensive-copy `SchemaSnapshotContent`, `StoredSchemaSnapshot`, graph/table/neighbourhood projections, and `CanonicalSchemaSnapshotHasher.Hash`. Follow the existing `CanonicalPlanHasher` binary writer: prefix `DataPitcher.SchemaSnapshot.v1`; ordinal-sort unordered tables, unique constraints, and foreign keys by their own encoded bytes; retain declared column and key-column order; write only schema/table/column/type/nullability/key/FK/enforcement/trust fields; SHA-256 the bytes with `Convert.ToHexString`. The classifier never accepts `Healthy` as an input. A direct test must prove identity and provider version are display facts but do not alter the snapshot hash.

4. - [ ] **Step 4: Run the focused Core suite and confirm it passes.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ConnectionModelsTests"`. Expected: `Passed` with zero failures, including every required/optional/cleanup branch and all canonical-hash perturbations.

5. - [ ] **Step 5: Commit the Core contracts.** Run: `git add src/DataPitcher.Core/Connections src/DataPitcher.Core/Schema/SchemaSnapshotModels.cs tests/DataPitcher.UnitTests/Connections/ConnectionModelsTests.cs && git commit -m "feat: define connection and schema contracts"`.

### Task 2: Persist reference-only profiles and resolve secrets only at the use boundary

**Files:**
- Create: `src/DataPitcher.Infrastructure/Connections/SecretReferenceResolver.cs`, `src/DataPitcher.Infrastructure/Connections/ConnectionProfileStore.cs`, `src/DataPitcher.Infrastructure/Migrations/0004-connections-and-schema.sql`, `tests/DataPitcher.UnitTests/Infrastructure/ConnectionProfileStoreTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/SecretReferenceResolverTests.cs`
- Modify: `src/DataPitcher.Infrastructure/Persistence/ControlRows.cs`, `src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs`, `src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj`, `tests/DataPitcher.UnitTests/Infrastructure/ControlDatabaseMigratorTests.cs`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/ConnectionProfileStoreTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/SecretReferenceResolverTests.cs`

1. - [ ] **Step 1: Write failing SQLite and resolver tests.** Use `ControlDatabaseFixture` to create, read, update, and delete a profile; assert create/replay with the same idempotency key returns the original ID, an update/delete with an old ETag fails, and every summary omits both locator and resolved content. Query SQLite directly and assert the environment name or mounted path is stored but each sentinel secret is absent. Set a uniquely named environment variable and create a temporary allowed-root file to prove each resolver branch; reject a file that escapes the allowed root and a missing variable/file. Capture an exception containing all sentinels and assert resolver/store logs and `Activity` tags contain none.

```csharp
[Fact]
public async Task ConnectionProfileStore_WhenRead_ReturnsNoSecretLocatorOrContent()
{
    using var fixture = new ControlDatabaseFixture();
    fixture.Migrator.Apply();
    var store = new ConnectionProfileStore(fixture.Database, fixture.Clock);
    var profile = await store.CreateAsync(new ConnectionProfileDraft(
        "Source", "postgresql", new(SecretReferenceKind.EnvironmentVariable, "DP_TEST_SECRET"),
        "app", "__datapitcher"), "connection-create-01", CancellationToken.None);

    var summary = await store.GetSummaryAsync(profile.ConnectionId, CancellationToken.None);
    Assert.DoesNotContain("DP_TEST_SECRET", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    Assert.DoesNotContain("password-redaction-sentinel", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.Ordinal);
}
```

2. - [ ] **Step 2: Run the persistence tests and confirm the missing-store failure.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ConnectionProfileStoreTests|FullyQualifiedName~SecretReferenceResolverTests"`. Expected: compilation fails with `CS0246` because `ConnectionProfileStore` and `SecretReferenceResolver` do not exist.

3. - [ ] **Step 3: Add the migration, stores, resolver, and safe observability.** Register embedded migration version 4 after version 3. Create `ConnectionProfiles` with profile ID, display name, provider ID, reference kind, reference locator, business/staging schemas, version, health state, assessment mode/role, safe identity/version, capability JSON, cleanup-failure code, and timestamps. Create `SchemaScans` and `SchemaSnapshots` in this migration now, with `ConnectionId` foreign keys, `(ConnectionId, IdempotencyKey)` unique, and no column capable of holding resolved secret content. Add corresponding internal LINQ-to-DB rows.

```csharp
public interface ISecretReferenceResolver
{
    Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}

public sealed class SecretReferenceResolver(string secretsRoot) : ISecretReferenceResolver
{
    public async Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) => reference.Kind switch
    {
        SecretReferenceKind.EnvironmentVariable =>
            Environment.GetEnvironmentVariable(reference.Locator)
                ?? throw new InvalidOperationException("Configured secret is unavailable."),
        SecretReferenceKind.FileMounted => await ReadMountedAsync(reference.Locator, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(reference)),
    };

    private async Task<string> ReadMountedAsync(string locator, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(secretsRoot);
        var path = Path.GetFullPath(locator);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Mounted secret reference is outside the configured root.");
        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
```

`ConnectionProfileStore` defines `ConnectionProfileDraft` before its first use and exposes `CreateAsync`, `GetSummaryAsync`, `ListSummariesAsync`, `UpdateAsync`, `DeleteAsync`, `GetProfileAsync`, and `SaveAssessmentAsync`; each public member receives a direct test. Create persists only the incoming reference and has no resolver call path. Update replaces a reference atomically with its version write, and delete removes profile-owned scans/snapshots only after ETag comparison succeeds. It uses parameterized SQLite values, ordinal profile IDs, version-based ETags, and fixed exceptions without reference locators or database exception messages. Emit one structured completion record and one `Activity` with only connection ID, provider ID, state, capability names, and fixed error code. Never pass `SecretReference`, a resolved string, an exception, or an arbitrary connection-string builder into logging or telemetry. Tests inspect all profile/scan table columns from `sqlite_master` as well as row values, preventing a later secret-payload migration.

4. - [ ] **Step 4: Run the in-process storage and resolver suite and confirm it passes.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ConnectionProfileStoreTests|FullyQualifiedName~SecretReferenceResolverTests|FullyQualifiedName~ControlDatabaseMigratorTests"`. Expected: all CRUD, ETag, idempotency, migration, allowed-root, missing-secret, and sentinel telemetry assertions pass with zero failures.

5. - [ ] **Step 5: Commit reference-only persistence.** Run: `git add src/DataPitcher.Infrastructure/Connections src/DataPitcher.Infrastructure/Persistence/ControlRows.cs src/DataPitcher.Infrastructure/Migrations/0004-connections-and-schema.sql src/DataPitcher.Infrastructure/Migrations/ControlDatabaseMigrator.cs src/DataPitcher.Infrastructure/DataPitcher.Infrastructure.csproj tests/DataPitcher.UnitTests/Infrastructure && git commit -m "feat: persist secure connection profiles"`.

### Task 3: Implement real provider probes and preserve bounded catalog discovery

**Files:**
- Create: `src/DataPitcher.Providers.PostgreSql/PostgreSqlConnectionProbe.cs`, `src/DataPitcher.Providers.PostgreSql/PostgreSqlSchemaIntrospector.cs`, `src/DataPitcher.Providers.SqlServer/SqlServerConnectionProbe.cs`, `src/DataPitcher.Providers.SqlServer/SqlServerSchemaIntrospector.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlConnectionProbeTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerConnectionProbeTests.cs`
- Modify: `src/DataPitcher.Providers.PostgreSql/PostgreSqlCatalogReader.cs`, `src/DataPitcher.Providers.SqlServer/SqlServerCatalogReader.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`
- Test: `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlConnectionProbeTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerConnectionProbeTests.cs`, `tests/DataPitcher.Providers.PostgreSql.IntegrationTests/PostgreSqlCatalogReaderTests.cs`, `tests/DataPitcher.Providers.SqlServer.IntegrationTests/SqlServerCatalogReaderTests.cs`

1. - [ ] **Step 1: Write real-database probe and query-bound tests.** For each containerized provider, create a restricted source principal and a staging schema, probe it with a five-second connection/command timeout, and assert `CanConnect`, identity, version, scalar command, business-read permission, and staging create/verify/drop capability are reported. Create a principal that cannot drop the probe object and assert the result is `Unhealthy` with a non-empty fixed cleanup-failure code, never a swallowed exception. Assert a probe creates an unpredictable staging object, verifies it through the catalog, and leaves none after successful cleanup. Add 1, then 51 base tables to each fixture, capture provider command traffic, and assert the catalog reader issues exactly three tagged metadata commands for either size; assert neither capture contains `COUNT(*)`.

```csharp
[Fact]
public async Task ProbeAsync_WhenStagingCleanupFails_ReportsAnExplicitSafeFailure()
{
    await using var scope = await _fixture.CreateDropDeniedScopeAsync();
    var request = scope.Request(TransferMode.ResumableStaged, ConnectionRole.Source);
    var evidence = await new PostgreSqlConnectionProbe().ProbeAsync(request, CancellationToken.None);

    Assert.NotNull(evidence.CleanupFailureCode);
    Assert.DoesNotContain("Password=", evidence.CleanupFailureCode!, StringComparison.Ordinal);
}
```

2. - [ ] **Step 2: Run the provider suites and confirm the missing-implementation failure.** Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlConnectionProbeTests|FullyQualifiedName~PostgreSqlCatalogReaderTests" && dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerConnectionProbeTests|FullyQualifiedName~SqlServerCatalogReaderTests"`. Expected: each build fails with `CS0246` because its provider probe type does not exist.

3. - [ ] **Step 3: Implement probes, introspector adapters, and ordinal catalog fixes.** Each probe clones the resolved connection string into its provider builder and sets an explicit five-second connect timeout and command timeout. It opens one real connection, executes `SELECT 1`, then reads database identity and provider version with provider-native scalar commands. It checks permissions through native permission functions without modifying a business schema. For required durable staging, generate an identifier from `Guid.NewGuid().ToString("N")`, safely quote the configured staging schema, create a one-column probe table, verify its catalog existence, and drop it in `finally`. A drop exception produces a fixed `staging_cleanup_failed` code and prevents a healthy result; no cleanup exception is ignored and neither raw SQL nor an exception message is returned.

```csharp
public sealed class PostgreSqlConnectionProbe : ICapabilityDetector
{
    public async Task<ConnectionProbeEvidence> ProbeAsync(ConnectionProbeRequest request, CancellationToken cancellationToken)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(request.ResolvedConnectionString)
        {
            Timeout = 5,
            CommandTimeout = 5,
        };
        await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var scalar = new Npgsql.NpgsqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
        _ = await scalar.ExecuteScalarAsync(cancellationToken);
        return await PostgreSqlProbeCommands.ReadEvidenceAsync(connection, request, cancellationToken);
    }
}
```

`PostgreSqlProbeCommands.ReadEvidenceAsync` is a private static method in `PostgreSqlConnectionProbe.cs`; it owns the identity, permission, create, verify, and `finally` cleanup commands and constructs the returned evidence. `PostgreSqlSchemaIntrospector` creates a short-lived data source, calls the existing reader once, and converts its definitions/foreign keys to `SchemaSnapshotContent`; `SqlServerSchemaIntrospector` does the equivalent with the existing reader. Do not add a per-table loop around either reader. Preserve the PostgreSQL reader’s three commands and add the same three-command assertion for SQL Server. Replace touched table/name equality and sorting in both catalog readers with ordinal comparers; do not refactor their unrelated mapping behavior.

For PostgreSQL use `current_database()`, `version()`, `has_database_privilege(current_database(), 'CONNECT')`, `has_schema_privilege(@businessSchema, 'USAGE')`, and `has_schema_privilege(@stagingSchema, 'CREATE')`; for SQL Server use `DB_NAME()`, `SERVERPROPERTY('ProductVersion')`, and `HAS_PERMS_BY_NAME` scoped to the database and schemas. Business-read and source/target staging capabilities must be independently represented in `Available`, not inferred from `SELECT 1`. Give test principals only the grant under test so an administrator fixture connection cannot make a permission test pass. Raw driver command recording remains test-only and exists only for the bounded-query assertion.

4. - [ ] **Step 4: Run both real-provider suites and confirm all permission, cleanup, and bounded-query checks pass.** Run: `dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --filter "FullyQualifiedName~PostgreSqlConnectionProbeTests|FullyQualifiedName~PostgreSqlCatalogReaderTests" && dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerConnectionProbeTests|FullyQualifiedName~SqlServerCatalogReaderTests"`. Expected: both report zero failures; each scan path uses three metadata queries regardless of added table count and no initial query requests an exact row count.

5. - [ ] **Step 5: Commit provider verification and scan adapters.** Run: `git add src/DataPitcher.Providers.PostgreSql src/DataPitcher.Providers.SqlServer tests/DataPitcher.Providers.PostgreSql.IntegrationTests tests/DataPitcher.Providers.SqlServer.IntegrationTests && git commit -m "feat: probe provider connections and schema"`.

### Task 4: Orchestrate health rechecks, durable scans, immutable snapshots, and transfer preflight

**Files:**
- Create: `src/DataPitcher.Infrastructure/Connections/ConnectionHealthService.cs`, `src/DataPitcher.Infrastructure/Schema/SchemaSnapshotStore.cs`, `src/DataPitcher.Infrastructure/Schema/SchemaScanWorker.cs`, `tests/DataPitcher.UnitTests/Infrastructure/SchemaScanWorkerTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ConnectionHealthServiceTests.cs`
- Modify: `src/DataPitcher.Infrastructure/Connections/ConnectionProfileStore.cs`, `src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs`, `src/DataPitcher.Infrastructure/Worker/JobWorker.cs`, `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`, `tests/DataPitcher.UnitTests/Worker/WorkerContractsTests.cs`, `tests/DataPitcher.UnitTests/Worker/RecoveryCoordinatorTests.cs`
- Test: `tests/DataPitcher.UnitTests/Infrastructure/SchemaScanWorkerTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ConnectionHealthServiceTests.cs`, `tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs`

1. - [ ] **Step 1: Write failing orchestration and preflight tests with fakes.** With an in-memory SQLite profile/scan store, queue the same scan idempotency key twice and assert one scan ID; process it through a fake provider whose introspector returns unordered transfer metadata; assert `Queued`, `Running`, and `Completed` state transitions, an immutable hash/snapshot, graph direction child-to-parent, table detail, and depth-bounded neighbourhood. Re-run with changed metadata and assert a distinct hash; run with an introspector exception containing a sentinel and assert a fixed failed state with no sentinel. Test `ConnectionHealthService.TestAsync` and `RecheckAsync` persist only classifier-derived state. Finally make a fake transfer revalidator fail and assert `JobWorker` revalidates both profile IDs after `LoadAsync` but before either source/target session factory is invoked.

```csharp
[Fact]
public async Task JobWorker_WhenConnectionRevalidationFails_DoesNotOpenEitherDatabaseSession()
{
    var fixture = new WorkerFixture { Revalidator = new FailingConnectionRevalidator() };
    await fixture.Worker.RunOneAsync(CancellationToken.None);

    Assert.Equal(0, fixture.SourceFactory.OpenCalls);
    Assert.Equal(0, fixture.TargetFactory.OpenCalls);
    Assert.Equal(1, fixture.Revalidator.Calls);
}
```

2. - [ ] **Step 2: Run the orchestration tests and confirm the absent-worker failure.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~SchemaScanWorkerTests|FullyQualifiedName~ConnectionHealthServiceTests|FullyQualifiedName~JobWorkerTests"`. Expected: compilation fails with `CS0246` because `SchemaScanWorker` and `ITransferConnectionRevalidator` do not exist.

3. - [ ] **Step 3: Implement scan processing and the mandatory server-derived preflight.** `SchemaSnapshotStore` serializes only `SchemaSnapshotContent` to an immutable snapshot row, persists its canonical hash, and provides `GetAsync`, `GetGraphAsync`, `GetTableAsync`, and `GetNeighbourhoodAsync`; all lookups use ordinal schema/table comparison and fail safely for a foreign snapshot/profile pair. `SchemaScanWorker.ProcessNextAsync` atomically claims a queued row, resolves its reference, invokes exactly one registered provider introspector, hashes, saves, and completes the scan. Its hosted loop only calls `ProcessNextAsync`; tests call the public one-shot method. It never requests a row count and never caches a mutable snapshot instance.

```csharp
public interface ITransferConnectionRevalidator
{
    Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken);
}

public sealed record TransferRun(
    Guid JobId, Guid RunId, string ManifestSealHash, bool SupportsDurableResume,
    Guid SourceConnectionId, Guid TargetConnectionId, TransferMode TransferMode);
```

`ConnectionHealthService` resolves the reference only after moving health to `Checking`, asks the provider detector for evidence, applies Task 1 requirements, and saves the resulting assessment. It maps all provider/resolver failures to fixed codes without reading exception messages. Its `RevalidateAsync` probes source then target against the loaded run’s exact mode/role and throws `ConnectionNotHealthyException` if either is not `Healthy`; `JobWorker.RunClaimAsync` calls it immediately after `catalog.LoadAsync` and before `targets.OpenAsync` or `sources.OpenKeysetAsync`. There is no overload taking a client-supplied boolean. Extend worker fakes and tests for every constructor/member branch created by this change.

Define `ConnectionNotHealthyException` here with no secret-bearing public properties and a fixed base message. When source passes but target fails, the test observes two probe requests using the loaded mode and respectively `Source` and `Target` roles; when source fails, the worker fails safely without opening a session. `SchemaScanWorker` records only `connection_failed`, `unsupported_provider`, or `schema_scan_failed`; it never persists a provider exception string. A completed scan replayed with its same idempotency key returns its original receipt and never calls the resolver or catalog reader again.

4. - [ ] **Step 4: Run the focused worker and orchestration suite and confirm it passes.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~SchemaScanWorkerTests|FullyQualifiedName~ConnectionHealthServiceTests|FullyQualifiedName~JobWorkerTests"`. Expected: snapshots are immutable and graph-correct, scan replays are idempotent, failures are safe, and a transfer cannot open sessions before server revalidation succeeds.

5. - [ ] **Step 5: Commit schema and health orchestration.** Run: `git add src/DataPitcher.Infrastructure/Connections src/DataPitcher.Infrastructure/Schema src/DataPitcher.Infrastructure/Worker/WorkerContracts.cs src/DataPitcher.Infrastructure/Worker/JobWorker.cs tests/DataPitcher.UnitTests/Infrastructure tests/DataPitcher.UnitTests/Worker/JobWorkerTests.cs && git commit -m "feat: orchestrate schema scans and connection health"`.

### Task 5: Expose safe connection and snapshot routes with explicit policy metadata

**Files:**
- Create: `src/DataPitcher.Api/Contracts/ConnectionSchemaApplication.cs`, `tests/DataPitcher.Api.IntegrationTests/ConnectionSchemaEndpointTests.cs`
- Modify: `src/DataPitcher.Api/Contracts/ApiContracts.cs`, `src/DataPitcher.Api/Contracts/IDataPitcherApplication.cs`, `src/DataPitcher.Api/Endpoints/EndpointGroups.cs`, `src/DataPitcher.Api/Program.cs`, `src/DataPitcher.Api/DataPitcher.Api.csproj`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`, `tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/ConnectionSchemaEndpointTests.cs`, `tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs`

1. - [ ] **Step 1: Write failing endpoint and metadata tests.** In the existing in-memory authenticated host, prove one behavior per route: create/list/read/update/delete profile; test/recheck profile; read server health; create schema scan returns `202 Accepted`; read scan; read snapshot, graph, one table detail, and its depth-bounded neighbourhood. Test `If-Match` is required for update/delete and an `Idempotency-Key` is required for create and scan creation. Test each ID-bearing route rejects a denied `ConnectionResource` before the fake application records an invocation. Extend the endpoint safety-net expected route inventory so every new route must have exactly one authorization mode and Problem Details metadata.

```csharp
[Fact]
public async Task CreateSchemaScan_WhenIdempotencyKeyIsPresent_ReturnsAccepted()
{
    var connectionId = Guid.NewGuid();
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/connections/{connectionId}/schema-scans");
    request.Headers.Add("Idempotency-Key", "scan-01");
    using var response = await _client.SendAsync(request, CancellationToken.None);

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var scan = await response.Content.ReadFromJsonAsync<SchemaScanResponse>();
    Assert.NotNull(scan);
    Assert.Equal(connectionId, scan!.ConnectionId);
}
```

2. - [ ] **Step 2: Run the API route suites and confirm the missing-contract failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ConnectionSchemaEndpointTests|FullyQualifiedName~EndpointAuthorizationSafetyNetTests"`. Expected: compilation fails with `CS0246` because `SchemaScanResponse` and `ConnectionHealthResponse` do not exist.

3. - [ ] **Step 3: Add safe transport records, production adapter, registrations, and exact routes.** Define request-only `SecretReferenceRequest(SecretReferenceKind Kind, string Locator)`, `CreateConnectionRequest`, `UpdateConnectionRequest`, and `ConnectionCheckRequest(TransferMode Mode, ConnectionRole Role)`. Define response-only `ConnectionResponse`, `ConnectionHealthResponse`, `SchemaScanResponse`, `SchemaSnapshotResponse`, `SchemaGraphResponse`, `SchemaTableResponse`, and `SchemaNeighbourhoodResponse`; none has a locator, resolved secret, connection string, exception message, token, or arbitrary provider payload. Extend `IDataPitcherApplication` and its test fake with one cancellable method per endpoint before mapping handlers. `ConnectionSchemaApplication` translates to the Task 4 services and preserves fixed failures.

```csharp
public sealed record ConnectionResponse(
    Guid ConnectionId, string DisplayName, string ProviderId,
    SecretReferenceKind SecretReferenceKind, string Health, string ETag);
public sealed record SchemaScanResponse(
    Guid ScanId, Guid ConnectionId, string State, Guid? SnapshotId, string? SnapshotHash, Uri StatusUri);

public interface IDataPitcherApplication
{
    Task<ConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<ConnectionResponse> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectionResponse>> ListConnectionsAsync(CancellationToken cancellationToken);
    Task<ConnectionResponse> UpdateConnectionAsync(Guid connectionId, UpdateConnectionRequest request, string ifMatch, CancellationToken cancellationToken);
    Task DeleteConnectionAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken);
    Task<ConnectionHealthResponse> TestConnectionAsync(Guid connectionId, ConnectionCheckRequest request, CancellationToken cancellationToken);
    Task<ConnectionHealthResponse> RecheckConnectionAsync(Guid connectionId, ConnectionCheckRequest request, CancellationToken cancellationToken);
    Task<ConnectionHealthResponse> GetConnectionHealthAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<SchemaScanResponse> CreateSchemaScanAsync(Guid connectionId, string idempotencyKey, CancellationToken cancellationToken);
    Task<SchemaScanResponse> GetSchemaScanAsync(Guid connectionId, Guid scanId, CancellationToken cancellationToken);
    Task<SchemaSnapshotResponse> GetSnapshotAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);
    Task<SchemaGraphResponse> GetSnapshotGraphAsync(Guid connectionId, Guid snapshotId, CancellationToken cancellationToken);
    Task<SchemaTableResponse> GetSnapshotTableAsync(Guid connectionId, Guid snapshotId, string schema, string table, CancellationToken cancellationToken);
    Task<SchemaNeighbourhoodResponse> GetSnapshotNeighbourhoodAsync(Guid connectionId, Guid snapshotId, string schema, string table, int depth, CancellationToken cancellationToken);
}
```

Map these exact paths: `GET|POST /api/connections`; `GET|PUT|DELETE /api/connections/{connectionId:guid}`; `POST /api/connections/{connectionId:guid}/tests`; `POST /api/connections/{connectionId:guid}/rechecks`; `GET /api/connections/{connectionId:guid}/health`; `POST /api/connections/{connectionId:guid}/schema-scans`; `GET /api/connections/{connectionId:guid}/schema-scans/{scanId:guid}`; `GET /api/connections/{connectionId:guid}/snapshots/{snapshotId:guid}`; `GET /api/connections/{connectionId:guid}/snapshots/{snapshotId:guid}/graph`; `GET /api/connections/{connectionId:guid}/snapshots/{snapshotId:guid}/tables/{schema}/{table}`; and `GET /api/connections/{connectionId:guid}/snapshots/{snapshotId:guid}/tables/{schema}/{table}/neighbourhood`. Bind `depth` only from a query value, require an integer from 1 through 3, and return validation Problem Details otherwise. Apply `ConnectionsRead` to reads/health, `ConnectionsWrite` to profile mutation/test/recheck, `SchemaWrite` to scan creation, and `SchemaRead` to scan/snapshot reads; each route declares its policy directly and each connection-ID handler performs resource authorization before application invocation. Add standard 400/401/403/500 Problem Details metadata plus 409 where ETag or profile/snapshot mismatches occur. Register both concrete providers only in API, preserving the architecture leaf rule.

4. - [ ] **Step 4: Run the endpoint and authorization suites and confirm they pass.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ConnectionSchemaEndpointTests|FullyQualifiedName~EndpointAuthorizationSafetyNetTests"`. Expected: all route statuses, ETags, idempotency headers, `202` scan behavior, cancellation propagation, resource checks, and explicit authorization metadata assertions pass with zero failures.

5. - [ ] **Step 5: Commit the secure HTTP surface.** Run: `git add src/DataPitcher.Api/Contracts src/DataPitcher.Api/Endpoints/EndpointGroups.cs src/DataPitcher.Api/Program.cs src/DataPitcher.Api/DataPitcher.Api.csproj tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs tests/DataPitcher.Api.IntegrationTests/ConnectionSchemaEndpointTests.cs tests/DataPitcher.Api.IntegrationTests/EndpointAuthorizationSafetyNetTests.cs && git commit -m "feat: expose connections and schema snapshots"`.

### Task 6: Prove complete secret containment and close all slice coverage

**Files:**
- Create: `tests/DataPitcher.Api.IntegrationTests/SecretLeakageTests.cs`
- Modify: `src/DataPitcher.Api/Errors/ApiProblems.cs`, `src/DataPitcher.Api/Program.cs`, `tests/DataPitcher.Api.IntegrationTests/ApiWebApplicationFactory.cs`, `tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs`, `tests/DataPitcher.Api.IntegrationTests/ProblemDetailsTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ConnectionHealthServiceTests.cs`
- Test: `tests/DataPitcher.Api.IntegrationTests/SecretLeakageTests.cs`, `tests/DataPitcher.Api.IntegrationTests/OpenApiTests.cs`, `tests/DataPitcher.Api.IntegrationTests/ProblemDetailsTests.cs`, `tests/DataPitcher.UnitTests/Infrastructure/ConnectionHealthServiceTests.cs`

1. - [ ] **Step 1: Write the sentinel containment tests before changing error handling.** Inject five unique values: password, token, client secret, a full connection string, and content read from an environment/file reference. Configure the fake application, fake resolver, and fake provider to receive them and to throw exceptions containing all five. Enumerate every connection/schema endpoint with a valid request, collect each successful and failing body plus every response/content header, request the protected OpenAPI document, and recursively inspect every document string, example, enum, default, description, and schema name. Capture log state and activity tags around health/scan execution. Assert `Assert.DoesNotContain` for every sentinel in each collected artifact; separately assert API responses never include the reference locator. Cover `ConnectionNotHealthyException` in the closed Problem Details classifier with `connection_failed` and a fixed message.

```csharp
[Fact]
public async Task SecretSentinels_AppearInNoResponseHeaderErrorOrOpenApiDocument()
{
    _factory.Application.ConnectionFailure = new InvalidOperationException(string.Join('|', ForbiddenSentinels));
    var artifacts = await CollectAllConnectionSchemaArtifactsAsync();
    using var openApi = await GetOpenApiDocumentAsync();
    artifacts.AddRange(Strings(openApi.RootElement));

    foreach (var sentinel in ForbiddenSentinels)
        Assert.DoesNotContain(artifacts, value => value.Contains(sentinel, StringComparison.Ordinal));
}
```

2. - [ ] **Step 2: Run containment tests and confirm the intended red failure.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SecretLeakageTests|FullyQualifiedName~OpenApiTests|FullyQualifiedName~ProblemDetailsTests"`. Expected: FAIL because the new fake failure message is not yet classified by a fixed safe `ConnectionNotHealthyException` mapping or because a newly added transport still exposes a locator; no compilation or collection failure is acceptable.

3. - [ ] **Step 3: Close the remaining safe-mapping and OpenAPI paths without adding secret-bearing fields.** Map `ConnectionNotHealthyException`, reference-resolution failure, provider probe failure, and scan failure by concrete type to `ApiErrorClass.Connection` or `UnsupportedProviderFeature`, using only fixed titles/details/codes. Do not ever copy exception message, `Data`, inner exception, provider error, connection builder, request body, or reference into Problem Details. Keep OpenAPI generated from the safe request/response records; do not add `Example`, `Default`, XML text, or descriptions containing credentials. Extend the test factory’s logger/activity collectors only for tests, reset them in `finally`, and retain the real services’ safe fixed observability fields from Task 2.

4. - [ ] **Step 4: Run focused redaction tests and both coverage gates.** Run: `dotnet test tests/DataPitcher.Api.IntegrationTests/DataPitcher.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SecretLeakageTests|FullyQualifiedName~OpenApiTests|FullyQualifiedName~ProblemDetailsTests" && dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ConnectionHealthServiceTests" && ./scripts/test-all.sh`. Expected: all focused tests pass; the final command reports `Merged coverage: line=100% branch=100% method=100%` and exits 0.

5. - [ ] **Step 5: Commit the containment proof.** Run: `git add src/DataPitcher.Api/Errors/ApiProblems.cs src/DataPitcher.Api/Program.cs tests/DataPitcher.Api.IntegrationTests tests/DataPitcher.UnitTests/Infrastructure/ConnectionHealthServiceTests.cs && git commit -m "test: prove connection secret containment"`.

## Self-Review

- Coverage: Tasks 1–6 assign a focused test to every new public type, record, property, constructor, method, route handler, classifier branch, provider command branch, worker branch, migration path, and API authorization/Problem Details/OpenAPI behavior; Task 6 runs the merged 100 percent line, branch, and method gate.
- Requirement coverage: secret references and sentinel containment are Tasks 1, 2, and 6; real short-timeout health/capability/permission/staging cleanup is Task 3; server-only immediate pre-transfer revalidation is Task 4; bounded three-query scan and immutable canonical snapshots are Tasks 1, 3, and 4; all requested API endpoints, policies, ETags, idempotency, and `202 Accepted` scan response are Task 5.
- Deferrals: frontend graph layout, approximate/exact row counts, schema migration, plan/selection UI integration, and cross-provider compatibility work remain out of scope. Type and method-name consistency was checked across all tasks: `ConnectionProfile`, `ConnectionHealthService`, `SchemaScanWorker`, `SchemaSnapshotStore`, `ConnectionProbeRequest`, `ITransferConnectionRevalidator`, `SchemaScanResponse`, and their endpoint names are introduced before later use.
