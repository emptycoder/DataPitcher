# ADR 0002: Exact Set Verification Scope and Limits

## Title

Exact-set verification captures committed direct writes, with bounded integrity checks and explicit limits.

## Status

Accepted.

## Date

2026-09-01.

## Context

DataPitcher transfers a sealed row manifest into SQL Server and PostgreSQL targets. Its guarantee is no DataPitcher insert or update lies outside the planned manifest. Verification precedes Succeeded; bulk-copy completion alone is insufficient.

DataPitcher supports DirectFast, which performs provider-native bulk writing directly to a business table; ResumableStaged, the production default, which batches into DataPitcher-owned target staging before a set-based apply; and an optional provider-specific ServerSide optimization. StrictExact must prove that the keys DataPitcher actually affected equal the sealed PlannedWriteManifest. That proof must be limited to facts the target database records at the job commit boundary and must not claim isolation from unrelated concurrent writers.

## Decision

### 1. Capture affected keys inside the write transaction

SQL Server captures keys and action labels with OUTPUT INTO; PostgreSQL uses RETURNING. Capture is trusted only after the write transaction commits. Output ordering is unspecified.

On SQL Server, enabled target triggers prohibit bare OUTPUT but not OUTPUT INTO. Its capture destination must have no enabled triggers, foreign-key participation, CHECK constraints, or enabled rules. OUTPUT values precede triggers and can be delivered for a later-rolled-back statement, so pre-commit capture is not proof.

PostgreSQL RETURNING contains only inserted or updated rows; ON CONFLICT DO NOTHING rows and rows excluded by an update predicate are absent. Trigger DML is never returned.

Use separate UPDATE and INSERT statements with OUTPUT INTO or RETURNING, not MERGE. SQL Server uses UPDATE-then-INSERT-WHERE-NOT-EXISTS under HOLDLOCK to avoid MERGE's documented concurrency and unique-index hazards. PostgreSQL needs separate statements for broad support: MERGE RETURNING with merge_action() starts in version 17.

### 2. Exclude DirectFast from StrictExact

SqlBulkCopy exposes counts and progress, not destination keys; Npgsql binary COPY likewise cannot return keys. Equating affected and streamed keys requires insert-only writes, immutable unique manifest keys, exact one-to-one streaming, identity preservation, a unique target key, no ignored duplicates or fired triggers, and one caller-managed transaction committed only after the complete stream.

The inference fails with triggers, ignored duplicates, suppressed or redirected rows, or non-transactional copy with internally committed batches. A source-manifest join proves supplied rows, not committed rows. DirectFast is Standard only, never StrictExact: StrictExact selection blocks plan sealing and never silently downgrades.

### 3. Block StrictExact in the presence of side effects

If a target table can fire a user trigger, DataPitcher may cause out-of-manifest rows. Neither output facility returns nested-DML causal closure. Static trigger inspection is unsound: triggers may call functions, dynamic SQL, remote services, or further triggers. Log or WAL auditing needs privileged prior configuration and still misses effects.

Presence detection is bounded and cheap; proving effects is not. StrictExact is blocked for a planned table with a user trigger, PostgreSQL rewrite rule, or applicable cascading server-side write path. Standard mode may proceed only with an explicit, clearly stated downgrade.

### 4. Publish a bounded StrictExact guarantee

DataPitcher publishes the following guarantee: “For every job reported Succeeded in StrictExact mode, the target database's recorded keys for DataPitcher's direct INSERT and UPDATE operations equal the sealed PlannedWriteManifest, and every planned outcome was verified at the job's commit boundary. This does not assert that rows outside the manifest remained unchanged by other sessions, nor does it persist after that boundary; StrictExact is unavailable when triggers, rules, cascades, or other server-side code can create unverified writes.”

An unqualified assertion that no row outside the manifest changed would be false. Proving that DataPitcher did not write outside the manifest is different from proving that no row outside the manifest changed, because concurrent sessions can write independently.

### 5. Verify foreign-key integrity within a bounded scope

Verify only transferred child rows and referenced-key domains changed by the job. For each outbound foreign key, fetch child rows by manifest key and anti-join final foreign-key values against the referenced unique key, honoring MATCH SIMPLE and MATCH FULL NULL. For inbound foreign keys, inspect existing child rows only if DataPitcher changed a referenced key, restricted to affected old and new tuples.

An index on inbound child foreign-key columns is required to promise bounded I/O; otherwise block or disclose a possible full scan. Record whether each SQL Server constraint is enabled and trusted and each PostgreSQL constraint validated. This excludes pre-existing global violations, unrecorded trigger writes, cross-database application relations, and later concurrent damage.

### 6. Verify identity and sequence safety

Success proves the next generated key is safely beyond occupied values in generator direction without rewinding an already-safe generator. SQL Server inspects current identity, increment, and column extremum: positive current is at least the existing maximum; negative current at most the existing minimum. Account for empty-table next-value semantics; reseed only when needed.

PostgreSQL discovers the OWNED sequence from the catalog, not its name. Explicit values do not advance it, so direction-aware setval with correct is_called semantics is mandatory; preserve an already-ahead sequence. GENERATED ALWAYS AS IDENTITY needs OVERRIDING SYSTEM VALUE for explicit inserts. Reject shared or cycling sequences unless separately supported. setval is immediately visible and not rolled back, so exclude concurrent sequence users while adjusting. Otherwise later inserts can collide or silently duplicate without uniqueness enforcement.

### 7. Defer cross-provider checksums

The initial release omits cross-provider checksums. Deterministically canonicalizable types are NULL, booleans, integers, UUIDs, raw binary with length, dates, exact decimals normalized for trailing and negative zero, and timestamps at an agreed common precision.

Reject by default float and real unless exact same-width IEEE bits are contractual; numeric and date infinities; original timezone offset, which PostgreSQL stores only as a UTC instant; collation-semantic text; and JSON or JSONB. SQL Server JSON and PostgreSQL json preserve textual detail, while jsonb strips whitespace, reorders keys, and collapses duplicates. XML, spatial, and provider-specific variants need individual specifications. Key-set equality plus bounded foreign-key verification is the guarantee; checksums are later and separately specified.

### 8. Apply the transfer-mode guarantee matrix

## Guarantee Matrix

| Transfer mode | SQL Server | PostgreSQL |
| --- | --- | --- |
| DirectFast | Standard only. Precondition: atomic external transaction, no fired triggers, no ignored duplicates, exact explicit-key stream. Otherwise Blocked. | Standard only. Precondition: atomic COPY with stop-on-error, no triggers, exact explicit-key stream. Otherwise Blocked. |
| ResumableStaged | StrictExact achievable. Precondition: set-based OUTPUT INTO, atomic apply-verify-commit, side-effect-free local target. | StrictExact achievable. Precondition: RETURNING with separate statements, or PostgreSQL 17+ MERGE, and a side-effect-free local target. |
| ServerSide | StrictExact for local set-based DML with OUTPUT INTO under the same transaction and schema preconditions; remote targets Blocked. | StrictExact for local RETURNING-capable DML; FDW or remote targets require explicit capability proof or are Blocked. |

## Consequences

StrictExact is a commit-boundary provider-recorded key-set guarantee, not a claim about later table state. Before sealing, inspect capture support, mode, triggers or rules, cascade paths, transaction boundaries, foreign-key indexes and status, and identity or sequence behavior. A missing precondition blocks StrictExact.

ResumableStaged is the normal StrictExact path because it supports set-based apply and in-transaction capture. DirectFast retains Standard mode. StrictExact adds capture, foreign-key checks, metadata inspection, and generator safety, but avoids an unbounded global scan and value-level equivalence until checksums are specified.

## Alternatives Considered

Bulk-copy counts, progress, and source-manifest joins were rejected as exact-set evidence because they observe supplied, not committed, keys. Universal MERGE was rejected for SQL Server hazards and PostgreSQL availability. Trigger inspection, log or WAL auditing, and treating output as trigger closure cannot reliably prove all causal writes.

Global foreign-key validation was rejected because it includes pre-existing violations and can require unbounded I/O. Unconditional generator reseeding can move an already-safe generator backward. Cross-provider checksums lack a shared deterministic representation for several types.

## Verification

Test SQL Server OUTPUT INTO and PostgreSQL RETURNING for inserts, updates, conflict-do-nothing, and predicate exclusions; compare only committed keys and ignore ordering. Test capture-destination restrictions and StrictExact blocking for enabled target triggers.

Test DirectFast sealing on both providers: StrictExact blocks, never downgrades. Test Standard preconditions and separate applies under SQL Server HOLDLOCK and supported PostgreSQL versions, including action labels.

Test metadata detection for triggers, PostgreSQL rewrite rules, and applicable cascades; Standard reporting of its downgrade; and concurrent writes that show StrictExact does not assert unrelated rows unchanged.

Test outbound anti-joins for valid and invalid final values, including MATCH SIMPLE and MATCH FULL NULL; inbound checks for changed keys and old/new tuples; constraint status; and missing inbound-index plan behavior.

Test positive and negative SQL Server identities, empty tables, safe values, and reseeding. Test PostgreSQL owned-sequence discovery, explicit values, direction-aware adjustment, is_called, GENERATED ALWAYS, shared/cycling rejection, and concurrent-user exclusion.

Test checksum-safe acceptance and unsafe default rejection. Test all matrix combinations, including blocked remote SQL Server targets and PostgreSQL FDW or remote targets lacking proof. Verify the published commit-boundary scope rather than infer global no-change.

## Open Questions

- Which provider-specific metadata checks define an applicable cascading server-side write path at plan-sealing time?
- What evidence qualifies as explicit capability proof for a PostgreSQL FDW or other remote target?
