# DataPitcher Test Coverage Matrix

This requirement traceability artefact maps requirements to normative rules, implementation tasks, and proving tests. Its absence allowed behavioural tests to be dropped from the Slice 1 plan. Update it as each slice completes; no feature is considered complete until its row is complete.

This records planned evidence, not results. No code exists or has run, so no row may be Passing or Complete. Status is **Planned**, **Deferred to Slice N**, or **Not scheduled**.

## Dependency closure behaviour

| Requirement ID | Description | Normative source (document and section) | Plan task | Tests | Status |
| --- | --- | --- | --- | --- | --- |
| DP-DEPEND-001 | Selecting a child includes its missing parent. | `dependency-semantics.md` §9 test 1 | Slice 1 Task 7 | §9 test 1 | Planned |
| DP-DEPEND-002 | Selecting a parent excludes inbound children by default. | `dependency-semantics.md` §9 test 2 | Slice 1 Task 7 | §9 test 2 | Planned |
| DP-DEPEND-003 | An enabled inbound relationship includes children. | `dependency-semantics.md` §9 test 3 | Slice 1 Task 7 | §9 test 3 | Planned |
| DP-DEPEND-004 | A null optional foreign key adds no parent. | `dependency-semantics.md` §9 test 4 | Slice 1 Task 7 | §9 test 4 | Planned |
| DP-DEPEND-005 | A composite foreign key resolves as one unit. | `dependency-semantics.md` §9 test 5 | Slice 1 Task 7 | §9 test 5 | Planned |
| DP-DEPEND-006 | A shared parent discovered twice is copied once. | `dependency-semantics.md` §9 test 6 | Slice 1 Task 7 | §9 test 6 | Planned |
| DP-DEPEND-007 | A trusted, enforced target parent terminates its branch. | `dependency-semantics.md` §9 test 7 | Slice 1 Task 7 | §9 test 7 | Planned |
| DP-DEPEND-008 | Ancestors behind a satisfied parent are pruned. | `dependency-semantics.md` §9 test 8 | Slice 1 Task 7 | §9 test 8 | Planned |
| DP-DEPEND-009 | A SkipExisting root expands no dependencies. | `dependency-semantics.md` §9 test 9 | Slice 1 Task 7 | §9 test 9 | Planned |
| DP-DEPEND-010 | An Upsert root expands its dependencies. | `dependency-semantics.md` §9 test 10 | Slice 1 Task 7 | §9 test 10 | Planned |
| DP-DEPEND-011 | A self-reference terminates without infinite expansion. | `dependency-semantics.md` §9 test 11 | Slice 1 Task 7 | §9 test 11 | Planned |
| DP-DEPEND-012 | A genuine row-level cycle is detected and handled. | `dependency-semantics.md` §9 test 12 | Slice 1 Task 7 | §9 test 12 | Planned |
| DP-DEPEND-013 | A table without a stable key is Blocked. | `dependency-semantics.md` §9 test 13 | Slice 1 Task 3 | §9 test 13 | Planned |
| DP-DEPEND-014 | A selected non-primary stable key works end to end. | `dependency-semantics.md` §9 test 14 | Not yet planned in detail | §9 test 14 | Deferred to Slice 5 |
| DP-DEPEND-015 | The target-existence probe compares under the target column's own collation: a staging key column declares that collation, so a column whose collation differs from the database default neither fails the probe nor matches under the wrong rule. | `dependency-semantics.md` §3 (collation implications) | Probe collation fix | `SqlServerClosureStoreTests` / `PostgreSqlClosureStoreTests` `ProbeTargetAsync_*` (unrelated row, case-sensitive column, case-insensitive column, empty target) | Planned |
| DP-DEPEND-016 | A skipped selected row is reported as "a target row holds its stable-key value", naming the constraint and columns and up to three source-to-target samples, never as "already exists". | `dependency-semantics.md` §3 (probing asks whether this row exists) | Skipped-root warning fix | `DependencyClosureTests` `Closure_CountsTheSkipExistingRootsItLeftOut`, `Closure_KeepsAtMostThreeSamples…`; `PlanSealingServiceTests` `SealAsync_WhenEverySelectedRowAlreadyExistsInTheTarget_SealsAnEmptyPlanAndSaysWhy` | Planned |
| DP-DEPEND-015 | Overlapping selections deduplicate into one set. | `dependency-semantics.md` §9 test 15 | Not yet planned in detail | §9 test 15 | Deferred to Slice 6 |
| DP-DEPEND-016 | A raw SQL join yields only declared root keys. | `dependency-semantics.md` §9 test 16 | Not yet planned in detail | §9 test 16 | Deferred to Slice 6 |
| DP-DEPEND-017 | Target probing issues no per-key existence query. | `dependency-semantics.md` §9 test 17 | Not yet planned in detail | §9 test 17 | Deferred to Slice 5 |
| DP-DEPEND-018 | A foreign key to a unique constraint resolves correctly. | `dependency-semantics.md` §9 test 18 | Not yet planned in detail | §9 test 18 | Deferred to Slice 5 |
| DP-DEPEND-019 | Multiple foreign keys between two tables stay distinct. | `dependency-semantics.md` §9 test 19 | Slice 1 Task 4 | §9 test 19 | Planned |
| DP-DEPEND-020 | A disabled relationship contributes no rows. | `dependency-semantics.md` §9 test 20 | Slice 1 Task 7 | §9 test 20 | Planned |
| DP-DEPEND-021 | DIAMOND transfers X when one demand path is satisfied. | `dependency-semantics.md` §9 fixture (a) | Slice 1 Task 7, step 5 | §9 fixture (a) | Planned |
| DP-DEPEND-022 | UNTRUSTED CONSTRAINT transfers the ancestor or names the constraint. | `dependency-semantics.md` §9 fixture (b) | Slice 1 Task 7 | §9 fixture (b) | Planned |
| DP-DEPEND-023 | CONCURRENT FRONTIER equals the single-threaded closure by row and generation. | `dependency-semantics.md` §9 fixture (c) | Not yet planned in detail | §9 fixture (c) | Deferred to Slice 11 |

## Dependency graph and relationship cases

| Requirement ID | Description | Normative source (document and section) | Plan task | Tests | Status |
| --- | --- | --- | --- | --- | --- |
| DP-GRAPH-001 | Composite keys preserve native ordered, unit identity. | `dependency-semantics.md` §8; §3 | Slice 1 Tasks 2 and 7 | Composite-key and composite-FK closure tests | Planned |
| DP-GRAPH-002 | Composite foreign keys follow all non-null columns together. | `dependency-semantics.md` §8 | Slice 1 Tasks 3 and 7 | §9 test 5; Task 7 composite-FK test | Planned |
| DP-GRAPH-003 | Nullable foreign keys add no dependency. | `dependency-semantics.md` §8 | Slice 1 Task 7 | §9 test 4; Task 7 nullable-FK test | Planned |
| DP-GRAPH-004 | Foreign keys can reference a unique, non-primary key. | `dependency-semantics.md` §8 | Not yet planned in detail | §9 test 18 | Deferred to Slice 5 |
| DP-GRAPH-005 | Self references are graph edges and terminate. | `dependency-semantics.md` §8 | Slice 1 Tasks 4, 5, and 7 | §9 test 11; graph and closure tests | Planned |
| DP-GRAPH-006 | Distinct foreign keys between a table pair remain distinct. | `dependency-semantics.md` §8 | Slice 1 Task 4 | §9 test 19; Task 4 graph-edge test | Planned |
| DP-GRAPH-007 | Shared dependencies are included once. | `dependency-semantics.md` §8 | Slice 1 Task 7 | §9 test 6; shared-parent test | Planned |
| DP-GRAPH-008 | Diamond paths retain demand from either branch. | `dependency-semantics.md` §8 | Slice 1 Task 7, step 5 | DIAMOND fixture | Planned |
| DP-GRAPH-009 | Cycles are detected without infinite closure expansion. | `dependency-semantics.md` §8; ADR 0003 §3 | Slice 1 Tasks 5 and 7 | §9 test 12; SCC and closure tests | Planned |
| DP-GRAPH-010 | Manually declared relationships participate like foreign keys. | `dependency-semantics.md` §8 | Not yet planned in detail | Required test not yet scheduled | Not scheduled |
| DP-GRAPH-011 | Source orphans transfer as-is without fabricated parents. | `dependency-semantics.md` §8 | Not yet planned in detail | Required test not yet scheduled | Not scheduled |
| DP-GRAPH-012 | Disabled relationships never contribute rows. | `dependency-semantics.md` §8 | Slice 1 Task 7 | §9 test 20; disabled-relationship test | Planned |

## Authentication, connection, schema, and selection

| Requirement ID | Description | Normative source (document and section) | Plan task | Tests | Status |
| --- | --- | --- | --- | --- | --- |
| DP-AUTH-001 | Support Entra ID, generic OIDC, and a production-excluded Development/Test provider. | ADR 0006 §§1, 3, 7 | Not yet planned in detail | ADR 0006 §10 verification matrix | Deferred to Slice 3 |
| DP-AUTHZ-001 | Normalize immutable identities and authorize through deny-safe permission bundles. | ADR 0006 §§5, 6, 8 | Not yet planned in detail | ADR 0006 verification: role and endpoint matrix | Deferred to Slice 3 |
| DP-CONN-001 | Server-verify source and target capabilities before planned work. | `architecture.md` §§2, 4 | Not yet planned in detail | Connection capability verification | Deferred to Slice 4 |
| DP-SCHEMA-001 | Discover transfer-relevant schema through bounded catalog queries. | `architecture.md` §5; ADR 0005 §2 | Not yet planned in detail | Catalog and metadata-query-count tests | Deferred to Slice 4 |
| DP-SELECT-001 | Produce exact distinct root keys from typed or read-only raw SQL selection. | `architecture.md` §2; `roadmap.md` Slice 6 | Not yet planned in detail | SQL generation, preview, and distinct-count tests | Deferred to Slice 6 |

## Plans, transfer, verification, recovery, and security

| Requirement ID | Description | Normative source (document and section) | Plan task | Tests | Status |
| --- | --- | --- | --- | --- | --- |
| DP-PLAN-001 | Seal an immutable plan after schema, conflict, cycle, and count validation. | `architecture.md` §2; ADR 0003 §§3-5 | Not yet planned in detail | Hash, invalidation, and cycle-strategy tests | Deferred to Slice 7 |
| DP-TRANSFER-001 | Use PostgreSQL native transfer writes with bounded execution. | ADR 0005 §4; `roadmap.md` Slice 8 | Not yet planned in detail | PostgreSQL writer and bounded-memory tests | Deferred to Slice 8 |
| DP-TRANSFER-002 | Use SQL Server native transfer writes with an external transaction. | ADR 0005 §3; `roadmap.md` Slice 9 | Not yet planned in detail | SQL Server writer and transaction tests | Deferred to Slice 9 |
| DP-VERIFY-001 | Verify PostgreSQL direct writes against the sealed manifest where eligible. | ADR 0002 §§1-6 | Not yet planned in detail | PostgreSQL affected-key and integrity tests | Deferred to Slice 8 |
| DP-VERIFY-002 | Verify SQL Server direct writes against the sealed manifest where eligible. | ADR 0002 §§1-6 | Not yet planned in detail | SQL Server affected-key and integrity tests | Deferred to Slice 9 |
| DP-VERIFY-003 | A NULL reference has no parent to resolve: verification never consults a parent table the run had no references into. | ADR 0002 §5; `dependency-semantics.md` §8 (nullable foreign keys) | Verifier defect fix | `SqlServerTransferVerifierTests` / `PostgreSqlTransferVerifierTests` `VerifyAsync_WhenEveryReferenceIsNullAndTheParentTableIsNotOnTheTarget_Passes` | Planned |
| DP-VERIFY-004 | Composite foreign-key columns are paired by the key's own referenced-column order, never by the parent's catalog order. | ADR 0002 §5; `dependency-semantics.md` §3 (composite key order) | Verifier defect fix | `…TransferVerifierTests` `VerifyAsync_WhenACompositeKeyReferencesParentColumnsOutOfTheirCatalogOrder_PairsThemByTheForeignKey` and `…OnlyMatchesTheParentInCatalogOrder_FailsNamingTheRelationship` | Planned |
| DP-TRANSFER-003 | The transfer schema reader names an invisible table (not a base table, or not permitted) and a missing stable-key column instead of a bare stable-key failure. | `architecture.md` §4 (server-verified capabilities); `dependency-semantics.md` §3 | Reader defect fix | `SqlServerTransferSchemaReaderTests` / `PostgreSqlTransferSchemaReaderTests` | Planned |
| DP-RECOVERY-001 | Persist target-local checkpoints and fence stale workers. | ADR 0001 Decision | Not yet planned in detail | Checkpoint, stale-fence, and state-transition tests | Deferred to Slice 2 |
| DP-RECOVERY-002 | Recover restarts and repair target mutations before resuming. | ADR 0001 Decision; ADR 0003 §6 | Not yet planned in detail | Fault-injection and mutation-journal tests | Deferred to Slice 11 |
| DP-SEC-001 | Exclude development authentication from production and protect every endpoint by default. | ADR 0006 §§7-8 | Not yet planned in detail | Production-artifact and endpoint-enumeration tests | Deferred to Slice 3 |

## How to use this document

Add a row before writing the test, not after. A slice may not exit with incomplete rows. A requirement without a normative source is a specification gap to resolve before implementation, not an excuse to skip the row.
