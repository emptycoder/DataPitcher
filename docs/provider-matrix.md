# Provider Compatibility Matrix

## 1. Status and how to read this

This document is the normative compatibility gate required by `architecture.md`. PostgreSQL support is **IMPLEMENTED and proven** for schema introspection, identifier quoting, typed staging, and the closure store. All 31 closure behavioural tests pass against a real PostgreSQL database. The implemented PostgreSQL slice is deliberately narrow: its catalog and typed staging currently cover integer and text key columns; it is not a business-row transfer implementation.

SQL Server is **DESIGNED but NOT YET IMPLEMENTED**. ADRs specify intended SQL Server behavior where needed, but no SQL Server provider project exists. “Implemented” means a provider path exists and has real-engine coverage; “Designed” means an ADR specifies the behavior but no provider path exists; “Untested” means neither is sufficient evidence for permission. A cell marked **Untested is a blocker, not a permission**, even if its capability value is otherwise expected to be Yes or Conditional.

## 2. Supported transfer paths

The supported paths are PostgreSQL-to-PostgreSQL and SQL Server-to-SQL Server. PostgreSQL-to-PostgreSQL is the only path with implemented provider work, and SQL Server-to-SQL Server remains a designed supported path pending implementation. Cross-provider transfer is **BLOCKED BY DEFAULT**.

Cross-provider transfer may be enabled only for an individual type mapping that this matrix records as explicitly tested. A value that round-trips within one engine can still lose precision, comparison semantics, ordering, timezone detail, generated-value behavior, or storage representation when translated to another. DataPitcher’s product guarantee is exactness, so a plausible conversion is not evidence and cannot authorize plan sealing.

## 3. Capability matrix

The value records the provider capability; the status records the DataPitcher evidence level. “Conditional” lists the prerequisite that must be established at plan sealing. Entries called Untested are deliberately not inferred beyond the ADRs and current PostgreSQL slice.

| Capability | PostgreSQL | SQL Server |
| --- | --- | --- |
| CanConnect | Yes — real Npgsql/Testcontainers connection. **Implemented**. | Conditional — engine smoke test exists, but no DataPitcher adapter. **Untested**. |
| CanReadSchema | Conditional — base tables and current integer/text mapping only. **Implemented**. | Conditional — catalog augmentation is required by ADR 0005. **Designed**. |
| CanReadBusinessRows | Conditional — closure reads keyed source rows within current type scope. **Implemented**. | Conditional — transfer reader is specified, not implemented. **Designed**. |
| CanCreateSourceStaging | Yes — typed owned staging for current key types. **Implemented**. | Conditional — typed owned staging is required. **Designed**. |
| CanDropSourceStaging | Yes — owned source objects are disposed. **Implemented**. | Conditional — owned staging cleanup is required. **Designed**. |
| CanCreateTargetStaging | Yes — typed target candidates for current key types. **Implemented**. | Conditional — target staging is required for staged apply. **Designed**. |
| CanDropTargetStaging | Yes — owned target candidates are disposed. **Implemented**. | Conditional — owned staging cleanup is required. **Designed**. |
| CanBulkInsert | Conditional — binary COPY for integer/text staging keys only. **Implemented**. | Conditional — `SqlBulkCopy`, explicit mappings, and external transaction. **Designed**. |
| CanPreserveIdentity | Conditional — explicit values, owned non-cycling sequence, and realignment. **Designed**. | Conditional — explicit identity handling and direction-aware reseed. **Designed**. |
| CanUseTransactions | Conditional — a caller-owned external transaction is required. **Designed**. | Conditional — external transaction; never internal bulk-copy transactions. **Designed**. |
| CanUseSnapshotIsolation | Conditional — isolation mode and long-snapshot behavior are not established. **Untested**. | Conditional — ADRs do not establish a provider implementation. **Untested**. |
| CanDeferConstraints | Conditional — every selected foreign key is enforced, validated, and already deferrable. **Designed**. | No — SQL Server has no deferrable foreign keys. **Designed**. |
| CanDisableConstraints | Conditional — authorization, recovery journal, and global recovery requirements. **Designed**. | Conditional — authorization, recovery journal, and global trust restoration. **Designed**. |
| CanRevalidateConstraints | Conditional — post-suspension validation has global scope where required. **Designed**. | Conditional — `WITH CHECK CHECK CONSTRAINT` is required to restore trust. **Designed**. |
| CanFireTriggers | Conditional — target side effects prohibit StrictExact; writer behavior is not implemented. **Designed**. | Conditional — target side effects prohibit StrictExact; writer behavior is not implemented. **Designed**. |
| CanSuppressTriggers | Conditional — only with suspension recovery and restoration. **Designed**. | Conditional — only with suspension recovery and restoration. **Designed**. |
| CanReseedGeneratedKeys | Conditional — owned non-shared, non-cycling sequence under concurrency exclusion. **Designed**. | Conditional — inspect direction/current value and reseed only when needed. **Designed**. |
| CanUseServerSideTransfer | Conditional — local RETURNING-capable DML; FDW or remote targets need proof. **Designed**. | Conditional — local set-based DML only; remote target is blocked. **Designed**. |
| SupportsDurableResume | Conditional — durable target checkpoint and staging contract are not implemented. **Untested**. | Conditional — durable target checkpoint and provider path are not implemented. **Untested**. |

## 4. Cycle strategy availability

Cycle selection is per cycle-breaking edge set after confirming that the planned row graph is genuinely cyclic. Ordered is always considered first; Blocked is a derived outcome, not a provider feature.

| Strategy | SQL Server | PostgreSQL | Required capability flag |
| --- | --- | --- | --- |
| Ordered | Available when the planned row graph is acyclic. | Available when the planned row graph is acyclic. | `RowGraphIsAcyclic` |
| Deferred | Not available; SQL Server has no equivalent. | Available only when every selected foreign key is already enforced, validated, and declared deferrable. | `SupportsDeferrableForeignKeys = false` on SQL Server; `CanDeferCycleBreakingFks` on PostgreSQL |
| Nullable two-phase | Conditional: nullable cleared columns, safe NULL semantics, no adverse checks/triggers, and one target transaction. | Conditional: same prerequisites. | `CanUseNullableFkTwoPhase` |
| Suspension | Conditional: authorization, target-local recovery journal, exact-state restoration, and global revalidation. | Conditional: same prerequisites. | `CanSafelySuspendFks` |
| Blocked | Derived fallback when no eligible edge set breaks every cycle. | Derived fallback when no eligible edge set breaks every cycle. | `MustBlockScc` |

## 5. Verification guarantee matrix

This is ADR 0002’s guarantee contract, not evidence that the corresponding provider writer exists today. **DirectFast cannot support StrictExact on either provider because native bulk copy returns no destination keys.** It is Standard only under the stated preconditions; StrictExact selection blocks plan sealing rather than silently downgrading.

**StrictExact is blocked entirely** when any planned target table has a user trigger, PostgreSQL rewrite rule, or applicable cascading server-side write path. The block reflects the inability of `OUTPUT INTO` or `RETURNING` to prove nested-DML causal closure.

| Transfer mode | SQL Server | PostgreSQL |
| --- | --- | --- |
| DirectFast | Standard only. Precondition: atomic external transaction, no fired triggers or ignored duplicates, and exact explicit-key stream; otherwise Blocked. | Standard only. Precondition: atomic COPY with stop-on-error, no triggers, and exact explicit-key stream; otherwise Blocked. |
| ResumableStaged | StrictExact achievable with set-based `OUTPUT INTO` and a side-effect-free local target. | StrictExact achievable with separate-statement `RETURNING`, or PostgreSQL 17+ MERGE, and a side-effect-free local target. |
| ServerSide | StrictExact for local set-based DML with `OUTPUT INTO` under the same transaction and schema preconditions; remote target Blocked. | StrictExact for local RETURNING-capable DML; FDW or remote targets require explicit capability proof or are Blocked. |

## 6. Type mapping status

These classifications are plan-sealing classifications, not a claim that all listed business-row mappings are implemented. Same-provider entries describe the intended identical-native-type path; current PostgreSQL implementation has real-engine coverage only for integer and text closure keys. Every cross-provider entry is **UNTESTED** because no explicit mapping test exists. PotentiallyLossy and Unsupported mappings block plan sealing by default; Exact, SafeWidening, and ExplicitConversionRequired require their recorded conditions and explicit approval.

| Type family | Same-provider status | Cross-provider status |
| --- | --- | --- |
| Integers | Exact for matching native width and signedness. | Exact candidate only for equal range and signedness; **UNTESTED**. |
| Decimal and numeric | Exact with identical precision, scale, and finite-value domain. | ExplicitConversionRequired for precision/scale/range differences; **UNTESTED**. |
| Floating point | Exact only for identical IEEE width and bit-preserving native path. | PotentiallyLossy; NaN, infinity, width, and bits need a contract; **UNTESTED**. |
| Boolean | Exact. | Exact candidate; **UNTESTED**. |
| Text and varchar with collation | Exact only with matching length and collation comparison semantics. | PotentiallyLossy because collation semantics differ; **UNTESTED**. |
| Binary | Exact for native binary bytes and length. | Exact candidate with length-preserving binary mapping; **UNTESTED**. |
| UUID | Exact. | Exact candidate; **UNTESTED**. |
| Date | Exact. | Exact candidate; **UNTESTED**. |
| Time | Exact with matching fractional-second precision. | SafeWidening only to equal-or-greater precision; otherwise PotentiallyLossy; **UNTESTED**. |
| Timestamp | Exact with matching fractional-second precision. | SafeWidening only to equal-or-greater precision; otherwise PotentiallyLossy; **UNTESTED**. |
| Timestamp with time zone | Exact only for the provider’s stored instant semantics. | ExplicitConversionRequired and PotentiallyLossy: PostgreSQL stores a UTC instant while offset detail can be lost; **UNTESTED**. |
| JSON and JSONB | Exact only within the same native storage semantics. | PotentiallyLossy: JSON text and JSONB normalization differ; **UNTESTED**. |
| Provider-specific types, including SQL Server rowversion | Unsupported for portable transfer; generated rowversion values are not writeable mappings. | Unsupported; **UNTESTED**. |

## 7. Known asymmetries

**SQL Server foreign-key deferral.** SQL Server has no deferrable foreign keys, so Deferred is never a SQL Server cycle strategy. A SQL Server cycle must use Ordered, eligible nullable two-phase handling, eligible suspension, or Blocked.

**PostgreSQL deferrability.** PostgreSQL deferrability is fixed when the constraint is defined or altered; `SET CONSTRAINTS` cannot defer a `NOT DEFERRABLE` constraint. DataPitcher does not change that metadata implicitly, so a non-deferrable constraint cannot authorize Deferred.

**PostgreSQL generated keys.** Explicit key values do not advance a PostgreSQL owned sequence, including values supplied through COPY. Realignment is mandatory under concurrency exclusion, otherwise a later generated value can collide; `setval` is immediate and not rolled back.

**Composite primary-key order.** The portable schema library orders PostgreSQL composite primary-key columns by physical position and is therefore wrong when declaration order differs. Both providers must read declaration order from their catalogs because ordered composite stable keys drive joins, probes, deduplication, and verification.

**Cross-provider checksums.** Cross-provider value checksums are deferred entirely because several common types lack deterministic shared canonicalization. Floats, infinities, timezone offsets, collation-semantic text, JSON/JSONB, XML, spatial values, and provider-specific variants cannot be treated as equal by an untested representation.

## 8. Environment note

On arm64 Apple Silicon, no native SQL Server container image is available; SQL Server runs under amd64 binary translation. Container memory has been measured, not estimated: a SQL Server 2022 container uses 1.08 GiB at readiness, and two run simultaneously at 2.28 GiB total, 29.8 percent of the 7.65 GiB allocation. Headroom for the two-source, two-target container test design is adequate based on this idle and light-load evidence; it is not a peak-load bound.
