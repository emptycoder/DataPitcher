# DataPitcher

DataPitcher is a targeted relational-data transfer tool, not a database cloning tool. A trusted operator selects an exact subset of rows from a source database; DataPitcher computes the smallest referentially complete dependency set, produces an immutable reviewable transfer plan, transfers exactly that data, and verifies the result.

## Current status

**This repository currently contains DOCUMENTATION ONLY.** No production code exists. Nothing has been built, run, or tested. The architecture and specification phase is complete; implementation has not started. [`docs/execution-state.md`](docs/execution-state.md) is the authoritative current-state record. The documents describe intended behavior, not running software: there is no solution file, executable, API, web client, test suite, package manifest, or container configuration in the tree. They require implementation and independent verification before supporting any operational conclusion or release claim.

## The core guarantee

The final transfer set is the roots the conflict policy will actually write, plus the smallest transitive set of rows required by enabled relationships that are not already satisfactorily present in the target. DataPitcher inserts or updates no row outside that set in the planned target business tables. A dependency is satisfactorily present only when its referenced key resolves and the corresponding target constraint exists, is enforced, and is trusted or validated. FailOnConflict blocks an existing root, SkipExisting writes and expands nothing for it, and Upsert writes and expands it. Target satisfaction deliberately favors minimality and referential integrity over refreshing source dependency values. This guarantee has documented limits, including its scope for concurrent writes, server-side effects, and verification modes; see [ADR 0002](docs/adr/0002-exact-set-verification-scope-and-limits.md).

## Dependency direction

A foreign key from `orders.customer_id` to `customers.id` creates an edge from Orders to Customers because an order depends on a customer. Selecting Orders pulls in the Customers they reference. Selecting Customers does **not** pull in all their Orders. Inbound children are included only when explicitly enabled. A table's presence in the graph never permits copying its whole contents: only demanded rows are eligible. SQL joins used to identify root keys do not make joined tables root transfer tables. This is the product's most commonly misunderstood rule.

## Supported databases

The supported database design covers SQL Server and PostgreSQL. Same-provider paths are fully supported by the architecture: SQL Server to SQL Server and PostgreSQL to PostgreSQL. Cross-provider transfers are blocked by default until a tested compatibility matrix exists. The target business schema must already exist; DataPitcher does not create or migrate it. Only base tables are transfer targets, source business tables are read-only, and source keys are retained rather than remapped.

## Supported authentication

The authentication design supports Microsoft Entra ID, generic OpenID Connect, and a Development-and-Test-only provider. The Development-and-Test provider is excluded from production builds. Authorization is permission-based and protects endpoints by default rather than relying on client-side controls. See [ADR 0006](docs/adr/0006-authentication-and-authorization-architecture.md).

For Development authentication, run `Authentication__Development__SigningKey="$(openssl rand -base64 48)" dotnet run --project src/DataPitcher.Api`.

## Repository layout

```text
.
├── docs/                         Architecture, specifications, and delivery records
│   ├── adr/                      Accepted architecture decision records
│   └── plans/                    Task-level implementation plans
├── README.md                     This repository entry point
└── src/ (Core, Application, ControlStore, Providers.*, Auth.*, Api), tests/, web/, scripts/
```

## Getting started

The verified development prerequisites are .NET SDK 10.0.400 and Node v25.2.1. Docker is **not installed** in the current environment. That blocks all container-based testing: Testcontainers integration tests, Docker Compose end-to-end runs, and Playwright browser tests cannot run here. It also means provider-backed discovery, transfer, and compatibility evidence cannot be produced in this environment. Docker does not prevent the provider-free domain algorithm and graph work, but it prevents checking claims that need a live PostgreSQL or SQL Server container or browser environment. This is an external environment blocker, not evidence that the documentation describes a working application.

The next action is to read [`docs/plans/2026-09-02-slice-1-domain-spine.md`](docs/plans/2026-09-02-slice-1-domain-spine.md) and execute Task 1. It creates the initial solution and domain-test scaffold, requires no Docker, and is the first step toward executable evidence rather than a claim that such evidence already exists.

## Git hooks and CI

Install [pre-commit](https://pre-commit.com/) 4.2.0 with `uv tool install pre-commit==4.2.0`; if prompted, run `uv tool update-shell` and restart the shell. Then run `pre-commit install --hook-type pre-commit --hook-type commit-msg --hook-type pre-push`. It is Python-based, so backend-only contributors do not need `npm install` just to commit.

Pre-commit checks staged backend files with `./scripts/format.sh --check` and staged frontend files with `npm --prefix web run lint`; commit messages must use `type(scope): summary`. Pre-push repeats the two fast checks for the whole repository. CI blocks on backend formatting, frontend linting, the .NET aggregate gate, and the frontend gate; container-based end-to-end jobs remain excluded.

## Documentation index

| Document | What it settles |
| --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | System scope, workflow, project boundaries, provider abstractions, and operating rules. |
| [`docs/dependency-semantics.md`](docs/dependency-semantics.md) | The normative minimal-closure, target-satisfaction, and relationship rules. |
| [`docs/spec-deviations.md`](docs/spec-deviations.md) | The 17 deliberate departures from the original specification and their rationale. |
| [`docs/threat-model.md`](docs/threat-model.md) | Design-phase threats, mitigations, and accepted residual risks. |
| [`docs/roadmap.md`](docs/roadmap.md) | The 11 delivery slices, dependencies, Docker status, and exit gates. |
| [`docs/execution-state.md`](docs/execution-state.md) | The authoritative implementation state, environment, blockers, and next action. |
| [`docs/test-coverage-matrix.md`](docs/test-coverage-matrix.md) | Requirement-to-rule, plan-task, and test traceability. |
| [`docs/plans/2026-09-02-slice-1-domain-spine.md`](docs/plans/2026-09-02-slice-1-domain-spine.md) | The task-level Domain Spine implementation plan. |
| [`docs/adr/0001-checkpoint-lives-in-the-target-database.md`](docs/adr/0001-checkpoint-lives-in-the-target-database.md) | ADR 0001: Checkpoint Lives in the Target Database. |
| [`docs/adr/0002-exact-set-verification-scope-and-limits.md`](docs/adr/0002-exact-set-verification-scope-and-limits.md) | ADR 0002: Exact Set Verification Scope and Limits. |
| [`docs/adr/0003-referential-cycle-strategy-per-provider.md`](docs/adr/0003-referential-cycle-strategy-per-provider.md) | ADR 0003: Referential Cycle Strategy per Provider. |
| [`docs/adr/0004-single-demand-driven-closure.md`](docs/adr/0004-single-demand-driven-closure.md) | ADR 0004: Single Demand-Driven, Target-Aware Closure. |
| [`docs/adr/0005-native-bulk-writers-and-linq2db-scope.md`](docs/adr/0005-native-bulk-writers-and-linq2db-scope.md) | ADR 0005: Native Bulk Writers and LINQ to DB Scope. |
| [`docs/adr/0006-authentication-and-authorization-architecture.md`](docs/adr/0006-authentication-and-authorization-architecture.md) | ADR 0006: Authentication and Authorization Architecture. |
| [`docs/adr/0007-frontend-architecture-and-coverage-isolation.md`](docs/adr/0007-frontend-architecture-and-coverage-isolation.md) | ADR 0007: Frontend Architecture and Coverage Isolation. |

## Known limitations

- A target-satisfied dependency row can hold stale non-key values and will not be refreshed. This is a deliberate product decision.
- Strict verification mode is blocked when a planned target table has triggers.
- DirectFast cannot support strict verification on either provider.
- Cross-provider value checksums are deferred.
- A trusted operator can exfiltrate data they are authorized to read; the design does not prevent that.
