# ADR 0003: Referential Cycle Strategy per Provider

## Title

Referential cycles are resolved from the planned row graph, with provider-specific capabilities applied only to the cycle-breaking edge set.

## Status

Accepted.

## Date

2026-09-01.

## Context

DataPitcher inserts an exact planned set of rows into SQL Server and PostgreSQL targets while respecting referential order. Foreign-key edges are directed child to parent, so parents generally precede children. Tables are grouped into SCCs with Tarjan's algorithm, and the condensed directed acyclic graph provides a topological transfer order.

An SCC with multiple tables, or a self-referencing table, cannot necessarily be ordered at the table level. That does not establish a planned-row cycle. The product must preserve referential correctness, avoid implicit constraint-definition changes, and recover safely from changed target enforcement.

## Decision

### 1. SQL Server does not support deferred foreign keys

SQL Server has no deferrable foreign keys; the Deferred strategy is PostgreSQL-only. SQL Server's enabled and trusted state is `is_disabled = 0` and `is_not_trusted = 0`. `NOCHECK CONSTRAINT` produces disabled and untrusted state (`1, 1`). `CHECK CONSTRAINT` without validation produces enabled but untrusted state (`0, 1`), as does adding a constraint `WITH NOCHECK`. Only `WITH CHECK CHECK CONSTRAINT` validates existing rows and, if validation succeeds, restores enabled and trusted state (`0, 0`).

An enabled but untrusted foreign key still checks subsequent relevant DML, while violations that predate enabling can remain. The optimizer will not use it for foreign-key simplification or join elimination; this preserves query correctness but can degrade plans. Applications also cannot assume global referential correctness. A disabled foreign key enforces nothing.

### 2. PostgreSQL deferrability is definition-time metadata

PostgreSQL deferrability is constraint metadata set when a constraint is created or by `ALTER`. `SET CONSTRAINTS` cannot defer a constraint declared `NOT DEFERRABLE`. DataPitcher therefore will not use Deferred for such a foreign key unless its definition is changed, and it will not make that change implicitly.

The capability probe inspects `pg_constraint` for the foreign key: `contype = 'f'`, matching `conrelid` and `confrelid`, and ordered `conkey` and `confkey`. It requires `condeferrable = true`. `condeferred` controls only the initial mode and is not required because DataPitcher defers explicitly. The probe also requires `convalidated = true`, enabled enforcement (`conenforced` where the server version exposes it, using version-aware probing), and enabled associated constraint triggers.

### 3. Test row cycles before table cycles and add Ordered

Table-level SCCs and row-level cycles are distinct. For example, `employee.manager_id -> employee.id` is a self-referencing table SCC, but its planned rows usually form a forest. DataPitcher adds a fifth outcome, Ordered: if the planned row graph is acyclic, it loads rows in row-topological order with every constraint fully enforced and uses no special strategy. This common case is always tested first.

DataPitcher bulk-stages planned row keys and self-referencing edges, then performs topological work provider-side, never through an application per-row loop. NULL parents and parents already present outside the plan are satisfied roots. Absent external parents are reported separately as missing references.

For a single-parent relationship, a recursive CTE expands from roots and assigns each child its parent's level plus one. Reaching every staged row proves that ascending level is a valid insertion order; an unreached subgraph contains a genuine row cycle. Multiple self-foreign-key cases use set-based Kahn frontier removal. Logical complexity and storage are O(V+E), recursion depth is at most V, and a single-parent edge has E at most V. Large plans require bulk staging, indexes on child and parent key columns, server-side execution, and explicit recursion, timeout, and temporary-space limits.

### 4. Select one strategy for the cycle-breaking edge set

Only after finding a genuinely cyclic planned row graph does DataPitcher select a strategy. Selection is per cycle-breaking edge set, not per table, in this order:

1. Deferred: PostgreSQL foreign keys that are enforced, validated, and deferrable.
2. Nullable two-phase foreign key: edges whose temporarily cleared columns are all nullable and whose temporary NULLs are safe under MATCH semantics, CHECK constraints, and triggers.
3. Constraint suspension: edges suspendable with authorization, permissions, durable recovery, and global revalidation.
4. Blocked: no earlier eligible edge set breaks every cycle.

For each candidate, remove all candidate-eligible edges from the row graph and test whether the residual graph topologically orders. This permits nullable edges to break every cycle while non-nullable edges remain ordered. If nullable edges do not break every cycle, the two-phase strategy is unavailable. Capability requirements are conjunctive across every table in the selected cycle-breaking set. Different strategies are not combined within one SCC.

### 5. Nullable two-phase requires transactional safety

Column nullability alone does not prove the two-phase strategy safe. A CHECK expression can reject NULL, and a trigger can enforce effective non-nullability. AFTER INSERT triggers can observe temporary NULLs, write incorrect audit or derived data, or make irreversible external effects; a later UPDATE trigger does not automatically repair them.

The insert, update, validation, and commit must occur in one target transaction. If insert and update commit separately, a crash leaves durable incorrect data. Without that transaction, or without a distributed transaction, the strategy violates the no-partial-state expectation and is not offered.

### 6. Constraint suspension needs a target-local recovery journal

Suspension and loading should remain in one target transaction. If suspension crosses a commit boundary, a durable target-local recovery journal must commit in the same target transaction as suspension. A control-database-only journal leaves an unavoidable commit gap.

On SQL Server, committed `NOCHECK` persists disabled and untrusted state; re-enabling without validation persists untrusted state. On PostgreSQL, committed `ALTER TABLE ... DISABLE TRIGGER` persists. `session_replication_role` is session-scoped and disappears on disconnect, though rows committed while checks were suppressed remain.

The journal records target identity; job and fencing token; constraint or trigger object identities; exact original states; expected suspended states; and phase. On restart, recovery fences the target, reads unfinished journals, compares objects with `sys.foreign_keys` or `pg_trigger`, blocks on any unexplained disabled object, repairs job-owned data, revalidates, restores exact original states, and closes the journal atomically. A revalidation failure ends the job as `VerificationFailed`.

### 7. Revalidation is global

Revalidation cost cannot be limited to transferred rows. PostgreSQL Deferred uses `SET CONSTRAINTS ... IMMEDIATE`, which checks pending transactional changes rather than scanning the table. Nullable two-phase checks the final UPDATE row by row while enforcement remains active. Both are comparatively cheap.

After SQL Server suspension, `WITH CHECK CHECK CONSTRAINT` examines all existing referencing rows to restore trust. `DBCC CHECKCONSTRAINTS` reports violations but does not restore trust. After PostgreSQL trigger suspension, `ENABLE TRIGGER` performs no historical validation, and `VALIDATE CONSTRAINT` scans only constraints marked `NOT VALID`; an already-valid catalog entry generally needs a full explicit foreign-key check or constraint recreation. Validation logically examines the whole child table plus parent matching. A transferred-row query proves only those rows, never global trusted or validated state. Indexes can improve physical plans but not this global scope.

### 8. Expose provider capabilities

Blocked is a derived outcome, not a provider feature.

## Capability Matrix

| Strategy | SQL Server | PostgreSQL |
| --- | --- | --- |
| Ordered | Available. Precondition: planned row graph is acyclic. Flag: `RowGraphIsAcyclic`. | Available. Precondition: planned row graph is acyclic. Flag: `RowGraphIsAcyclic`. |
| Deferred | Not available. Flag: `SupportsDeferrableForeignKeys = false`. | Available with precondition. Flag: `CanDeferCycleBreakingFks`. |
| Nullable two-phase | Available with precondition. Flag: `CanUseNullableFkTwoPhase`. | Available with precondition. Flag: `CanUseNullableFkTwoPhase`. |
| Suspension | Available with precondition. Flag: `CanSafelySuspendFks`. | Available with precondition. Flag: `CanSafelySuspendFks`. |
| Blocked | Derived fallback when no eligible edge set breaks every cycle. Flag: `MustBlockScc`. | Derived fallback when no eligible edge set breaks every cycle. Flag: `MustBlockScc`. |

## Consequences

Ordered preserves normal target behavior for most self-referencing plans. Planning makes staged row analysis, capability checks, transaction boundaries, privileges, recovery readiness, and global revalidation cost explicit prerequisites.

Suspension can leave a target globally untrusted, unvalidated, or unenforced after failure. The journal and `VerificationFailed` make this recoverable, not cheap. Targets failing a selected strategy's conjunctive requirements are Blocked.

## Alternatives Considered

Treating every table-level SCC as a row cycle was rejected because it unnecessarily changes enforcement for common acyclic row forests. Per-table selection was rejected because cycles are broken by edges and mixed nullability can leave a residual cycle.

Implicit PostgreSQL deferrability changes were rejected because this metadata is definition-time and not an implicit DataPitcher action. Suspension without a target-local journal cannot close the independent-store commit gap. Transferred-row validation cannot restore global trust or validation.

## Verification

Test SQL Server catalog states for trusted, disabled/untrusted, enabled/untrusted, and restored trusted foreign keys. Verify enabled-untrusted keys reject subsequent relevant DML, disabled keys do not enforce, and only validation recovers trust.

Test PostgreSQL probes against deferrable and `NOT DEFERRABLE` foreign keys, initial deferred and immediate modes, validated and unvalidated catalog states, enabled and disabled constraint triggers, and server versions with and without exposed `conenforced`. Verify Deferred is selected only when every chosen edge passes the probe.

Test row-graph detection with acyclic self-referencing forests, genuine self-cycles, missing external parents, NULL parents, and multiple-self-foreign-key graphs. Confirm provider-side staging reaches every acyclic row, residual-graph testing selects Ordered first and one eligible edge-set strategy, and no breaking set produces Blocked.

Test nullable two-phase against nullable columns that are rejected by CHECK constraints or triggers, and verify a simulated failure cannot commit between insert and update. Test suspension crash recovery after target-local journal persistence for SQL Server and PostgreSQL: recovery must fence the target, detect unexplained disabled objects, repair job-owned data, globally revalidate where required, restore exact states, and mark failed revalidation `VerificationFailed`. Measure that SQL Server trust restoration and PostgreSQL post-trigger-suspension validation examine global referential scope rather than only transferred rows.

## Open Questions

None.
