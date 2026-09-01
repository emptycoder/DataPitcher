# DataPitcher Architecture Overview

## 1. What DataPitcher is

DataPitcher is a targeted relational-data transfer tool, not a database cloning tool. A trusted operator selects an exact subset of rows in a source database. DataPitcher computes the smallest referentially complete dependency set, produces an immutable transfer plan, transfers exactly that data to a target database, and then verifies the result.

SQL Server and PostgreSQL are first-class providers. Fully supported paths are same-provider transfers: SQL Server to SQL Server and PostgreSQL to PostgreSQL. A cross-provider transfer runs only when it is covered by a documented, explicitly tested compatibility matrix. Potentially lossy or unsupported conversions block plan sealing by default.

The target business schema must already exist: DataPitcher neither creates nor migrates it. Source business tables are read-only. DataPitcher may write only to its source staging schema, named `__datapitcher`; planned target business tables; its target staging objects; and its control storage. It must never add columns or triggers to source business tables. Rows retain their source keys, and automatic key remapping is out of scope. Only base tables are transferable; views can assist selection but are never write targets.

The initial release excludes deletes, bidirectional sync, continuous replication, change-data capture, whole-database cloning, schema migration, key remapping, and destructive replace-by-delete.

## 2. The four-stage workflow

1. **Connections.** The operator configures source and target connections. Each has an independent health state: Unknown, Checking, Healthy, Degraded, or Unhealthy. A connection is Healthy only when every capability required by the current plan mode is available; it is Degraded when it works but lacks an optional optimization or safety feature; and it is Unhealthy when it cannot be used safely. The stage exits when both endpoints have been server-verified for the intended work.
2. **Explore and Select.** DataPitcher scans the schema, presents the table graph, and lets the operator construct exact root-row selections in the Selection Workbench. Selections can be saved and combined. The stage exits when the required root sets are saved and ready for dependency analysis.
3. **Dependencies and Plan.** The closure runs, the target is probed, and DataPitcher validates the target schema, detects conflicts and cycles, and computes exact counts. The stage exits only when an immutable plan is sealed.
4. **Transfer.** The operator starts the sealed plan; progress streams while transfer runs. Work may pause at committed batch boundaries, resume where supported, cancel, and verify. The stage exits at a terminal verified, cancelled, or failed job state.

Starting a transfer requires all of the following:

- The operator has permission.
- Source and target are server-verified Healthy, never merely represented by a frontend boolean.
- The plan is sealed and current.
- Schema validation passes.
- No blocker remains unresolved.
- Type mappings are safe.
- The cycle strategy is supported.
- Authentication remains valid.

Both connections are revalidated on the server immediately before transfer begins.

## 3. Solution structure and dependency rules

### Project layout

| Area | Responsibility |
| --- | --- |
| `src/DataPitcher.Core` | Provider-independent domain: graph algorithms, dependency rules, selection and plan semantics, state machines, conflict and verification rules, and the permission vocabulary. |
| `src/DataPitcher.Infrastructure` | Control-database persistence, job orchestration and queue ownership, auditing, schema snapshot and plan storage, and retry coordination. |
| `src/DataPitcher.Providers.SqlServer` | SQL Server catalog queries, dialect and identifier handling, typed staging DDL, native bulk writing, conflict and constraint handling, and verification SQL. |
| `src/DataPitcher.Providers.PostgreSql` | PostgreSQL catalog queries, dialect and identifier handling, typed staging DDL, native bulk writing, conflict and constraint handling, and verification SQL. |
| `src/DataPitcher.Auth.Abstractions` | External identity model and authentication-provider contracts. |
| `src/DataPitcher.Auth.EntraId` | Microsoft Entra ID scheme registration, configuration validation, and claim normalization. |
| `src/DataPitcher.Auth.OpenIdConnect` | Generic OpenID Connect scheme registration, configuration validation, and claim normalization. |
| `src/DataPitcher.Auth.Development` | Development authentication scheme registration, configuration validation, and claim normalization. |
| `src/DataPitcher.Api` | Minimal API registration, authentication and authorization wiring, transport records, OpenAPI, Server-Sent Events, and health checks. |
| `tests/DataPitcher.UnitTests` | Unit tests for domain behavior without database infrastructure. |
| `tests/DataPitcher.Api.IntegrationTests` | API boundary, transport, authorization, and host integration tests. |
| `tests/DataPitcher.SqlServer.IntegrationTests` | SQL Server provider integration tests. |
| `tests/DataPitcher.PostgreSql.IntegrationTests` | PostgreSQL provider integration tests. |
| `tests/DataPitcher.Auth.IntegrationTests` | Authentication-provider and authorization integration tests. |
| `tests/DataPitcher.RecoveryTests` | Checkpoint, interruption, resume, and recovery tests. |
| `tests/DataPitcher.PerformanceTests` | Discovery, closure, transfer, and throughput benchmark coverage. |
| `tests/DataPitcher.ArchitectureTests` | Enforceable project-boundary and dependency-rule tests. |
| `web` | React application with `api`, `app`, `auth` (`adapters`, `components`, `stores`), `components`, `features` (`connections`, `schema`, `selections`, `plans`, `jobs`, `administration`), `stores`, and `workers`; tests are split into unit, component, and end-to-end coverage. |
| `docker` | Container definitions and local dependency environments. |
| `scripts` | Development, test, maintenance, and operational scripts. |
| `docs`, including `docs/adr` | Architecture documentation and decision records. |

The control database is folded into Infrastructure rather than given its own project because it is SQLite-only with a single implementation; a project boundary around one implementation is ceremony. The four authentication projects are separate not for symmetry: the Development provider must be excludable from a production build artifact, and the Entra provider carries a dependency that generic OIDC must not inherit.

The following assertions are enforceable, and `DataPitcher.ArchitectureTests` exists to enforce them:

- Core depends on nothing: no ASP.NET, data-access library, or provider package.
- Infrastructure depends on Core.
- Each provider project depends on Core only.
- Auth.Abstractions depends on Core.
- Each authentication provider depends on Auth.Abstractions.
- Api depends on everything.
- Nothing depends on Api.
- No project other than Api, the composition root, may reference a concrete provider project.

The Core rule permits the closure algorithm, graph algorithms, plan hashing, and state machines to be developed and fully tested with no database or container at all.

## 4. Provider abstractions

| Abstraction | Responsibility |
| --- | --- |
| `ISchemaIntrospector` | Reads transfer-relevant schema metadata and produces snapshots. |
| `IDatabaseDialect` | Supplies provider-specific SQL syntax and semantic rules. |
| `IIdentifierQuoter` | Safely quotes provider identifiers. |
| `ICapabilityDetector` | Reports the connection capabilities available to a plan mode. |
| `ISourceWorkspace` | Creates and operates source-side staging and closure workspaces. |
| `ITargetWorkspace` | Creates and operates target-side staging workspaces. |
| `ITargetSchemaValidator` | Validates the planned target schema and its constraints. |
| `IBulkWriter` | Writes bounded batches using provider-native bulk facilities. |
| `IConflictStrategy` | Detects and applies the selected target-conflict behavior. |
| `IConstraintStrategy` | Applies provider-safe constraint handling for transfer ordering. |
| `ISequenceReseeder` | Reseeds generated-key sequences where required. |
| `IServerSideTransferStrategy` | Executes provider-supported server-side transfer paths. |

Provider-native code remains inside provider projects and never leaks into Core or Infrastructure. `ICapabilityDetector` reports: CanConnect, CanReadSchema, CanReadBusinessRows, CanCreateSourceStaging, CanDropSourceStaging, CanCreateTargetStaging, CanDropTargetStaging, CanBulkInsert, CanPreserveIdentity, CanUseTransactions, CanUseSnapshotIsolation, CanDeferConstraints, CanDisableConstraints, CanRevalidateConstraints, CanFireTriggers, CanSuppressTriggers, CanReseedGeneratedKeys, CanUseServerSideTransfer, and SupportsDurableResume.

## 5. Schema discovery and the graph

Discovery uses a constant, or provider-documented bounded, number of metadata queries independent of table count. No per-table, per-column, or per-foreign-key metadata-query loop is permitted. Exact row counts are excluded from the initial scan; approximate counts load lazily. Immutable schema snapshots are cached and identified by a canonical hash over transfer-relevant metadata only. They refresh on explicit request, TTL expiry, or detected drift.

The graph contains one node per base table and one directed foreign-key edge from child to parent. Adjacency is indexed in both directions. Tarjan's algorithm identifies strongly connected components; those components form a condensed directed acyclic graph with topological layers. Graph topology is returned to clients separately from all layout coordinates.

LINQ to DB schema discovery is a portable baseline only. Provider catalog queries augment it; ADR 0005 records what LINQ to DB does not provide and a result it returns that must not be trusted.

## 6. Staging workspaces

Dependency analysis happens primarily in the source database, never by pulling millions of keys into application memory. The preferred mode creates durable, plan-scoped typed key tables in source schema `__datapitcher`. Physical names are unpredictable and safely quoted; a logical-to-physical mapping is persisted in the control database. Tables use native key column types and a unique index across the complete stable key. There is no universal JSON key table.

Staging records stable key columns, origin kind, root selection identifier, discovery generation, representative predecessor relationship, target-existence state, final action, and expansion state. It is cleaned up after completion. A TTL cleanup worker removes abandoned objects but never objects owned by an active job.

If durable source staging permission is unavailable, DataPitcher uses session-scoped temporary key tables on a dedicated source connection. The plan is marked reduced-resume or non-durable; it does not claim durable resume, it re-analyses if the session is lost, and it exposes the limitation before sealing. DataPitcher owns target staging objects for bulk existence probing and the staged apply path.

## 7. Control database

The default control database is SQLite through LINQ to DB. It stores connection-profile metadata and secret references; non-secret authentication-provider metadata; external identity mappings; group-to-role mappings; schema snapshots; saved selections and versions; plan metadata and hashes; staging object mappings; job and table-job state; batch-checkpoint mirrors; audit events; verification results; and error summaries. It does not store business row payloads by default. Migrations are explicit versioned SQL scripts using a `SchemaVersion` table; no ORM is introduced merely for migrations.

The authoritative resume checkpoint lives in the target database. The control database holds only a derived mirror and must never be consulted for a correctness decision. ADR 0001 defines the target checkpoint and fencing rules.

## 8. Transfer execution

Jobs run through a persisted queue and background service, outside the HTTP request lifetime. States are Draft, Queued, Preparing, Running, Pausing, Paused, Cancelling, Cancelled, Verifying, Succeeded, Failed, and VerificationFailed; every transition is persisted. Starting requires an idempotency key. Only one worker owns a job. Pausing occurs only at committed batch boundaries, cancellation propagates to every database operation, and each worker owns its connections; mutable connections are never shared concurrently.

The pipeline is bounded: source data reader, optional lightweight conversion, bounded batch, then provider-native target writer. It has backpressure, cancellation, bounded memory, row and byte accounting, no unbounded queues, and no JSON serialization of transfer payloads. Initial queue capacity is one or two batches. Batches have both a maximum row count and a target payload byte size, initially 8 to 32 MiB and tuned from benchmarks.

Source reads select only insertable mapped columns, join the source table directly to the sealed manifest, order by stable key, and use sequential access. They never use offset pagination, large `IN` lists, whole-table materialization, or whole-transfer materialization.

The modes are DirectFast, ResumableStaged (the production default), and ServerSide. ADR 0002 defines what each can actually guarantee. Dependency order derives from topological groups of the condensed graph, so parents generally insert before children. Independent groups may run concurrently, capped by source and target pool limits, configured database pressure, CPU, memory, and provider limits. Single-worker execution is the safe default. Partitioning a large table additionally requires deterministic stable-key partitioning, provider support, safe target semantics, exact verification, and non-overlapping partitions.

## 9. Consistency, drift and verification

FrozenKeys is the default consistency mode. It seals identities eligible for transfer without freezing payload values: no new key may enter execution, and missing source keys are reported by policy. RepeatableReadRun uses a provider-appropriate consistent source transaction and documents its long-running snapshot costs. Where row-version metadata exists, later source changes can be detected with Warn, Fail, or AcceptLatest behavior.

DataPitcher always detects and blocks, rather than silently adapts to, source schema drift, target schema drift, database identity drift, missing staging objects, manifest mismatch, plan hash mismatch, and authentication or permission loss.

Verification is mandatory. A job becomes Succeeded only after verification passes; successful bulk-copy completion alone is not success. ADR 0002 defines verification scope, guarantee wording, and the per-mode guarantee matrix.

## 10. Cross-cutting concerns

Observability uses structured logs, metrics, and traces carrying correlation identifier; authentication provider; tenant; a safe immutable subject identifier; effective role names where safe; scan, selection, plan, job, table, and batch identifiers; provider; writer strategy; duration; rows; bytes; retry count; and error code. Full tokens and unnecessary personal data are never logged.

Errors are classified as Validation, Unauthenticated, Forbidden, IdentityProviderUnavailable, InvalidToken, TenantRejected, GroupResolutionFailed, AuthenticationConfiguration, Connection, SchemaDrift, UnsupportedProviderFeature, QuerySyntax, QueryTimeout, SourceIntegrity, TargetConflict, TypeConversion, ConstraintCycle, BulkWrite, TransientDatabaseFailure, Cancelled, Verification, or Internal. All map to Problem Details with status, a machine-readable code, a human-readable message, correlation identifier, relevant resource identifiers, and never secrets. Only transient failures are retried, only at idempotent boundaries, with bounded exponential backoff and jitter. A direct batch with unknown commit state is never blindly retried.

The API uses Minimal API route groups and typed request and response records. Queued long-running work returns `202 Accepted`. Every asynchronous handler and service accepts cancellation tokens. Mutable drafts use request validation and ETags or optimistic concurrency; non-repeatable commands use idempotency keys. A TypeScript client is generated from OpenAPI rather than duplicating handwritten transport types. Every protected endpoint has authorization-policy metadata.

## 11. Index of decision records

| ADR | Title | Decision |
| --- | --- | --- |
| 0001 | Resume checkpoint ownership and fencing | The resume checkpoint lives in the target database, with fencing. |
| 0002 | Exact-set verification scope and limits | Defines exact-set verification scope, limits, and transfer-mode guarantees. |
| 0003 | Referential cycle strategy per provider | Defines the provider-specific strategy for referential cycles. |
| 0004 | Single demand-driven closure | Adopts one demand-driven closure and supersedes the two-phase design. |
| 0005 | Native bulk writers and LINQ to DB limits | Requires native bulk writers and records limits of LINQ to DB. |
| 0006 | Authentication and authorization architecture | Defines authentication-provider and authorization architecture. |
| 0007 | Frontend architecture and coverage isolation | Defines frontend boundaries and coverage isolation. |

## Open Questions

No open questions are recorded in this overview; provider-specific and contested decisions belong in the ADRs.
