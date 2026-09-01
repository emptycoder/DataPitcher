# DataPitcher roadmap

## How to read this

Only Slice 1 is planned in task-level detail, in `docs/plans/2026-09-02-slice-1-domain-spine.md`. Later slices depend on the seams, models, and evidence established there, so planning them task-by-task now would be speculative. Each slice below states its goal, deliverables, dependencies, Docker status, and exit gate. The sequence follows the architecture in `docs/architecture.md` and ADRs 0001 through 0007.

## The Docker blocker

Docker is not installed on the current execution machine: the `docker` command is not found. This is a genuine external blocker, not a design problem. It prevents Testcontainers integration tests, Docker Compose end-to-end runs, and Playwright browser tests. Consequently, work is deliberately ordered so everything provable without a container comes first.

Unaffected work includes the domain layer, graph algorithms, the closure algorithm over an abstracted store, stable-key semantics, plan hashing, state machines, permission evaluation, claim normalization, in-process signed-token validation, and the whole frontend unit and component test layer.

## Slice 1 — Domain spine

The goal is to prove the dependency-closure algorithm and graph algorithms without a database. Deliverables are the solution scaffold; a `StableKey` value type with composite and ordering semantics; a schema model including foreign-key enforced and trusted flags; a dependency graph with bidirectional adjacency; Tarjan strongly connected components and condensation with topological layers; the `IClosureStore` abstraction with an in-memory fake; and the full closure algorithm, including the diamond correctness fixture. It also provides architecture tests enforcing that Core depends on nothing and a coverage-gate script that fails below 100 percent. It depends on nothing.

**Docker: not required**

**Exit gate:** All closure behavioural tests are green, and the coverage script exits non-zero when a test is deleted.

## Slice 2 — Control database and job state machine

This slice makes orchestration state durable. It delivers SQLite through LINQ to DB, explicit versioned SQL migration scripts with a `SchemaVersion` table, and a persisted job state machine covering Draft, Queued, Preparing, Running, Pausing, Paused, Cancelling, Cancelled, Verifying, Succeeded, Failed, and VerificationFailed. It also implements the lease and fence-token model defined by ADR 0001. It depends on Slice 1.

**Docker: not required** — SQLite runs in process.

**Exit gate:** Every state transition is covered, and a test proves that applying with a stale fence token aborts.

## Slice 3 — Authentication and authorization

The goal is deny-by-default authentication and authorization for Entra and generic OIDC. Deliverables are ADR 0006's three kept abstractions—provider registration, external-principal normalization, and optional group-membership resolution—the permission and role-bundle model, deterministic role-mapping precedence, group-overage fail-closed handling, and the development-provider production guard including build-artifact exclusion. An in-process signed-token issuer with a JWKS endpoint covers token validation. The endpoint authorization safety-net test enumerates every routed endpoint. This depends on Slices 1 and 2.

**Docker: partially blocked** — The in-process issuer works without Docker; containerized OIDC redirect-flow tests are blocked.

**Exit gate:** The full authorization matrix passes, and no protected endpoint lacks authorization metadata.

## Slice 4 — Provider adapters, PostgreSQL first

This slice establishes real schema truth. It delivers catalog-based schema introspection with a bounded metadata query count; correct composite primary-key ordering, which the portable library gets wrong per ADR 0005; unique constraints; generated and computed column kind; constraint trust and validation state; the dialect and identifier quoter; typed staging DDL; and capability detection. It depends on Slice 1.

**Docker: BLOCKED**

**Exit gate:** Introspection is verified against a real PostgreSQL container, and the metadata query count is proven independent of table count.

## Slice 5 — The database-backed closure store

The goal is to run Slice 1's algorithm against a real database. Deliverables are the real `IClosureStore` implementation, set-based expansion inside the source database, bulk target-existence probing, generation-stamped staging with a unique index over the complete stable key, and provenance persistence. It depends on Slices 1 and 4. Its behavioural tests already exist from Slice 1 and must be re-run unchanged against the real store; that re-run is the point of the abstraction.

**Docker: BLOCKED**

**Exit gate:** The Slice 1 test suite passes against the real store, and a test proves that no per-key existence query is issued.

## Slice 6 — Selections

This slice delivers exact row selection. It includes a typed query abstract syntax tree with nested boolean logic and the full operator set, provider-specific SQL generation through the dialect, raw SQL mode with a read-only single-statement contract, typed parameters, preview with a server-enforced maximum, and exact distinct stable-key counting. It depends on Slices 1 and 4.

**Docker: partially blocked** — The AST and SQL generation are pure and fully testable now; execution, preview, and counting are blocked.

**Exit gate:** Generated SQL executes correctly against both providers, and counting is proven distinct by stable key rather than by joined row.

## Slice 7 — Plan generation and sealing

The goal is an immutable, auditable approval artifact. Deliverables are target-schema validation with mapping-status classification, conflict detection, cycle-strategy selection per ADR 0003 including the Ordered outcome, exact counts, canonical plan hashing, and invalidation rules covering every material change. It depends on Slices 1, 4, 5, and 6.

**Docker: partially blocked** — Hashing, invalidation, and strategy selection are pure; target validation is blocked.

**Exit gate:** Equivalent canonical plans hash equally, every material change alters the hash, and a stale plan cannot be started.

## Slice 8 — PostgreSQL transfer execution

This slice moves PostgreSQL data and proves the result. It delivers the Npgsql binary COPY writer, a bounded producer-consumer pipeline with backpressure and byte-bounded batches, a target-side checkpoint written inside the apply transaction, fence-token enforcement, sequence realignment, and verification including affected-key capture through RETURNING. It depends on Slices 2, 4, 5, and 7.

**Docker: BLOCKED**

**Exit gate:** Actual affected keys equal the planned write manifest, and memory stays bounded under a large transfer.

## Slice 9 — SQL Server provider and transfer execution

The goal is provider parity. Deliverables are SQL Server catalog introspection, dialect, typed staging, the `SqlBulkCopy` writer always under an external transaction, identity preservation and reseeding, affected-key capture through OUTPUT INTO, and the constraint-trust handling from ADR 0003. It depends on Slices 4, 5, 7, and 8.

**Docker: BLOCKED**

**Exit gate:** The same exact-set equality assertion passes on SQL Server, and identity state is verified after transfer.

## Slice 10 — Frontend

This slice provides the operator experience. Deliverables are the React application shell, authentication adapters, the four-stage workflow, the dependency graph defaulting to the transfer-plan subgraph with ELK layout in a worker, the three-column Selection Workbench with Monaco, plan review, and an authenticated fetch-based SSE client writing into the query cache. It depends on Slices 3, 6, and 7.

**Docker: partially blocked** — Unit and component tests run without Docker; Playwright end-to-end testing is blocked.

**Exit gate:** Handwritten frontend code has 100 percent coverage, with all four thresholds enforced.

## Slice 11 — Recovery, hardening and evidence

The goal is to prove that the system survives failure and is honest about performance. Deliverables are restart recovery, deterministic fault injection with test-controlled barriers rather than timing races, the mutation-journal repair path, concurrency tests, security tests, reproducible performance benchmarks, mutation testing on critical logic, and completion of the operations and benchmarks documentation. It depends on all previous slices.

**Docker: BLOCKED** — Most proof work requires the unavailable containerized databases.

**Exit gate:** A crash injected between target commit and control-database write recovers correctly, and no unjustified surviving mutants remain in critical scope.

## Deferred from the original specification

Intentionally unscheduled items are cross-provider value checksums (see ADR 0002), automatic key remapping, schema migration of the target, deletes, bidirectional synchronization, continuous replication, change-data capture, and whole-database cloning.

## Unblocking

The only requirement is a working Docker daemon. Once available, every blocked slice becomes executable with no design change, because the `IClosureStore` seam from Slice 1 and the provider abstractions were designed for precisely this separation. The 100 percent coverage requirement across both stacks is the single largest cost driver in the roadmap and is estimated to add roughly 30 to 50 percent engineering effort on the frontend alone.
