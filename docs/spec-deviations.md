# Specification deviations

DataPitcher was specified in a detailed written specification. During architecture analysis, several specification requirements were found to be incorrect, unachievable as written, or internally contradictory. This document is the honest, auditable record of every deliberate deviation, so that a reviewer can challenge any of them rather than discovering them by surprise in the code. Each entry records the specification requirement, the chosen alternative, the reason for it, and where the full reasoning lives.

## Algorithm and data model

### D1

**SPEC REQUIRED:** A two-phase dependency closure: a source-only candidate closure, followed by a target-existence probe, followed by a second minimal final closure over write-eligible roots.

**INSTEAD:** Use one demand-driven, target-aware closure.

**REASON:** The specification says that “a candidate closure followed only by write-time skipping is insufficient.” That correctly rejects deferring conflict decisions to write time, but it does not establish a need for two closures. A single closure that consults target state while expanding also avoids write-time skipping and is equally correct. The two-phase design also depends on persisting the complete edge relation. At 50 million candidate rows and six enabled relationships, that is roughly 300 million edges and an estimated 15 to 45 GB of source staging. The candidate phase was not required for correctness, and its cost was severe.

**SEE:** ADR 0004.

### D2

**SPEC REQUIRED:** The property-based test property `FinalManifest is a subset of CandidateSet`.

**INSTEAD:** Delete that property.

**REASON:** D1 eliminates the candidate set, so this is not a property that is merely untested; it has no meaning. The specification’s property-based test list must be revised. The substantive guarantees it was intended to protect remain: every included row must be demanded through an enabled path, and a satisfied branch must contribute no unnecessary ancestors. Those properties are tested directly.

**SEE:** ADR 0004.

### D3

**SPEC REQUIRED:** Mark dependency rows already present in the target as `TargetSatisfied` and prune their ancestors.

**INSTEAD:** Prune only when the corresponding target foreign key exists, is enforced, and is trusted: the SQL Server not-trusted flag is clear or the PostgreSQL validated flag is set. Otherwise, treat the row as unsatisfied and transfer it.

**REASON:** Pruning a satisfied parent’s ancestors relies inductively on the claim that a parent already in the target implies that its required parents are also there. Only an enforced, trusted target constraint licenses that claim. An untrusted, unvalidated, disabled, or absent constraint breaks the induction and can yield a green transfer while the target remains referentially broken. The specification omits this precondition.

**SEE:** ADR 0004; section 5 of `docs/dependency-semantics.md`.

### D4

**SPEC REQUIRED:** Implicitly, that “existing in the target” is a well-defined satisfaction condition.

**INSTEAD:** Make an explicit product decision: DataPitcher guarantees **target referential integrity**, not source-graph fidelity, and satisfaction belongs to the pair of a row and its referenced unique key, not to the row’s mere existence.

**REASON:** These readings produce different pruning rules, and the specification does not choose between them. Under the selected reading, a satisfied parent may contain non-key values different from the source, and DataPitcher will not refresh them. Minimality and value refresh are incompatible goals. An operator that needs refreshed values must choose an Upsert policy for that table. This limitation must appear in user-facing documentation.

**SEE:** ADR 0004.

## Cycle handling

### D5

**SPEC REQUIRED:** Handle a strongly connected component with one of four strategies: deferred constraints, two-phase nullable foreign key load, constraint suspension, or `Blocked`.

**INSTEAD:** Add a fifth outcome, `Ordered`, and test it first.

**REASON:** The four listed strategies assume that the planned row graph is cyclic. A self-referencing table is a table-level strongly connected component, yet its planned rows are usually a forest rather than a cycle. The common case needs no special cycle strategy: row-topological ordering preserves full constraint enforcement. Without `Ordered`, that common case would be placed on an unnecessarily dangerous path.

**SEE:** ADR 0003.

### D6

**SPEC REQUIRED:** List deferred constraints as a generally available strategy.

**INSTEAD:** Offer deferred constraints only on PostgreSQL and only for foreign keys already declared deferrable.

**REASON:** SQL Server has no equivalent of deferrable foreign key constraints. PostgreSQL deferrability is constraint metadata fixed at definition time, so a `NOT DEFERRABLE` constraint cannot be deferred at runtime. DataPitcher will not implicitly alter a target constraint definition to make it deferrable.

**SEE:** ADR 0003.

## Libraries and providers

### D7

**SPEC REQUIRED:** Use LINQ to DB as the primary relational library, permit its bulk copy when proven by integration tests, prohibit silent row-by-row fallback, and record the actual writer strategy.

**INSTEAD:** Exclude LINQ to DB from the transfer write path. Provider projects call `SqlBulkCopy` and the Npgsql binary importer directly. LINQ to DB remains the primary library for querying, schema discovery, and the control database.

**REASON:** The required proof failed. The provider-specific bulk copy type is advisory rather than contractual: it can downgrade toward row-by-row processing when the provider connection cannot be unwrapped. Its result reports only a row count, not the strategy that ran. A native-or-fail guarantee is therefore unobtainable through that path, and the required strategy recording is impossible.

**SEE:** ADR 0005.

### D8

**SPEC REQUIRED:** Use LINQ to DB schema APIs as the portable baseline for schema discovery.

**INSTEAD:** Retain that baseline, but obtain composite primary-key ordering, unique constraints, generated and computed column kind, and constraint trust or validation state from provider catalog queries.

**REASON:** Required metadata is missing from the portable API. More importantly, the PostgreSQL schema provider derives primary-key ordering from physical column position rather than key declaration order. Composite key order is therefore wrong when those differ. Because the row-identity model depends on ordered composite stable keys, this is a correctness defect, not a convenience gap: it would silently corrupt staging joins, existence probes, deduplication, and verification.

**SEE:** ADR 0005.

## Durability and verification

### D9

**SPEC REQUIRED:** Persist batch checkpoints to the SQLite control database after target commit, with the instruction to “never assume an interrupted batch did not commit.”

**INSTEAD:** Write the authoritative checkpoint into the target database in the same transaction as the batch apply. Keep only a derived mirror in the control database; repair it from the target during recovery and never consult it for correctness.

**REASON:** Two durable stores without a distributed transaction cannot be safely ordered. One order leaves replay unknown; the other permits silent data loss. Removing the control database from the commit path eliminates the gap rather than mitigating it. A batch committed if and only if its target checkpoint advanced, making the specification’s warning vacuous.

**SEE:** ADR 0001.

### D10

**SPEC REQUIRED:** Implicitly, that a single-owner rule plus job state is sufficient to prevent concurrent writers.

**INSTEAD:** Assert a fencing token inside the target apply transaction, in addition to a control-database lease used only for scheduling.

**REASON:** A lease is advisory, while the target is the resource being mutated. A worker descheduled by the operating system can lose its lease, later wake, and commit. The fence makes the database adjudicate ownership, causing the stale worker to fail deterministically at commit without needing to observe its own expiry.

**SEE:** ADR 0001.

### D11

**SPEC REQUIRED:** Provide a `StrictExact` verification mode that proves affected keys equal the planned manifest, including trigger side effects where provider capabilities permit.

**INSTEAD:** Block `StrictExact` whenever a planned target table has any user trigger, rewrite rule, or applicable cascading server-side write path. Make it unavailable for `DirectFast` on either provider.

**REASON:** Neither provider output mechanism returns the causal closure of nested data modification. Static inspection of trigger bodies is unsound because they can invoke functions, dynamic SQL, remote services, or further triggers. Detecting trigger presence is bounded; proving its effects is not. Bulk copy also returns no destination keys, so `DirectFast` cannot observe what the server committed, only what DataPitcher supplied. The specification prohibits claiming exact-set guarantees when side effects cannot be verified; this applies that rule.

**SEE:** ADR 0002.

### D12

**SPEC REQUIRED:** Offer optional checksums using documented canonical representations.

**INSTEAD:** Defer cross-provider checksums from the initial release.

**REASON:** Deterministic cross-provider canonicalization excludes float and real, numeric and date infinities, original timezone offset, collation-semantic text, and JSON and JSONB. The JSON distinction is material: one provider’s binary JSON strips whitespace, reorders keys, and collapses duplicates, while textual JSON preserves them. A feature limited to the safe subset would provide little value and invite false confidence about everything excluded. Initial guarantees are key-set equality and bounded foreign-key verification.

**SEE:** ADR 0002.

## Authentication

### D13

**SPEC REQUIRED:** Eight authentication abstractions.

**INSTEAD:** Keep three: provider registration, external principal normalization, and optional group membership resolution. Delete five in favour of existing framework mechanisms or one concrete service.

**REASON:** Abstraction is earned by actual variability among the three required providers, and five of the eight abstractions have none. Options validation, authorization handling, and the current-actor accessor are already framework concerns. Role mapping is one provider-neutral control-database service, and audit enrichment is satisfied by the normalized actor. The genuine missing need was value contracts, not more interfaces.

**SEE:** ADR 0006.

### D14

**SPEC REQUIRED:** Make the Development and Test authentication provider fail startup in Production.

**INSTEAD:** Also exclude it from the production publish artifact and assert in CI that the artifact contains no such assembly.

**REASON:** A runtime environment check alone is insufficient: an attacker controlling environment variables can claim the Development environment. Excluding the code from the artifact removes the capability instead of merely guarding it.

**SEE:** ADR 0006.

## Frontend

### D15

**SPEC REQUIRED:** Support a dependency graph for very large schemas, without initially showing an unreadable full-database view.

**INSTEAD:** Make the default view the transfer-plan subgraph: selected tables plus transitive parent dependencies. Set a product target of at most roughly 200 simultaneously visible nodes, and do not treat visible-element culling as sufficient by itself.

**REASON:** Culling removes off-screen elements only. A fit-view that displays every node defeats it. The realistic soft ceiling is roughly 400 to 500 simple nodes before pan, zoom, and drag frame rate degrades.

**SEE:** ADR 0007.

## Process and environment

### D16

**SPEC REQUIRED:** Continuously implement through Testcontainers integration tests, Docker Compose end-to-end runs, and Playwright browser tests.

**INSTEAD:** Do not execute those container-dependent checks in the current environment. Order work so that everything provable without a container is completed first.

**REASON:** Docker is not installed on the execution machine: the `docker` command is not found. Under the specification’s own definition, a Docker daemon that cannot be started or accessed is a genuine external blocker. All container-dependent verification is therefore blocked. This does not affect the domain layer, graph algorithms, closure over an abstracted store, stable key semantics, plan hashing, state machines, permission evaluation, claim normalization, or the frontend unit and component test layer.

**SEE:** This entry records the environment assessment and planning consequence.

### D17

**SPEC REQUIRED:** A separate `DataPitcher.ControlDb` project.

**INSTEAD:** Fold the control database into `DataPitcher.Infrastructure`.

**REASON:** The control database is SQLite-only with one implementation. A project boundary around one implementation with no variability is ceremony that increases build surface without providing a seam.

**SEE:** This entry records the architectural rationale.

## How to challenge a deviation

Each deviation is a decision, not a fact. Anyone may contest one by disputing its stated reason. If a deviation is reversed, its ADR must be reversed as well, with the superseding decision recorded rather than silently editing the original decision.
