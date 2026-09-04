# ADR 0008: Sealed-Plan Safety and Run Verification

## Title

A sealed plan is versioned, refuses what it cannot prove, says what it left out, and a run is verified against it before it is reported Succeeded.

## Status

Accepted.

## Date

2026-09-04.

## Context

Four consecutive ordering patches each fixed one graph shape, and nothing validated a sealed plan as a whole or verified a run against it. A transfer could start from a plan sealed by an older algorithm, an ordering could be emitted that the target would reject, an invisible parent table or a source orphan could be dropped without a word, and a run could read rows outside the planned set. An operator saw a Succeeded job and a target that did not match the plan.

## Decision

### 1. Every sealed plan carries the sealing algorithm version

`TransferPlanContent.SealingVersion` is stamped from `CurrentSealingVersion` at seal time and is part of the canonical hash. Any change to what sealing means bumps the constant. Starting a job from a plan sealed by an older version is refused with `plan_stale` before anything is written, and the plan page shows the plan as invalidated with a blocker telling the operator to seal it again. A newer build never reinterprets an older plan.

### 2. Sealing refuses instead of emitting an invalid plan

Sealing fails, with a fixed code the operator can act on, whenever it cannot prove the plan:

| Code | Refused when |
| --- | --- |
| `unorderable_cycle` | Import ordering finds a cycle that no nullable edge can break. |
| `incomplete_graph` | A foreign key references a parent table the source catalog cannot see (SQL Server lists only tables the login may see; the parent appears as `?.#<object id>`). |
| `unique_key_collision` | A planned row's value on a target unique key belongs to a different target row. |
| `plan_in_use` | A job of the plan is still running. Re-sealing under a running job is refused, never allowed to clobber its keys. |
| `seal_rejected` | Any other precondition the sealing service states. |

A failure that is none of these is recorded as `seal_failed` with its detail. The failure code and detail are persisted on the plan record, so the plan page shows the seal as failed with its reason, not only a transient toast.

Import ordering runs Kahn's algorithm, defers nullable edges that close a cycle into a second phase that fills those columns after every row exists, and levels self-references so ancestors are written before descendants. Levelling maps the referenced columns to the stable key through one self-join, so any self-reference is levellable, not only one onto the primary key. A self-reference that is not nullable is levelled; the remaining ones are deferred.

### 3. What sealing leaves out is said on the plan

Warnings are part of the sealed content and appear on the plan page:

- `selection_empty`: the selection returned no rows.
- `roots_skipped`: selected rows the target already has under the stable key were skipped with their dependencies (conflict policy SkipExisting), with the count, the total, and sample keys so the operator can look them up in the target.
- `source_orphans`: planned rows whose foreign key value resolves to no parent in the source. The rows are transferred as-is; DataPitcher never fabricates a parent. When the target enforces that constraint, sealing refuses with `source_orphans` instead, because the write would fail.
- `target_constraint_untrusted`: a target constraint is missing, disabled or untrusted, so rows behind it are transferred rather than trusted to exist.

### 4. Unique-key collisions stop the run rather than skip the row

Skipping a planned row that collides with a different target row on a non-stable unique key silently breaks referential integrity: its children are written referencing a parent that is not the one they expect, or dangle. Sealing therefore refuses collisions it can see, the write path fails the run with `target_conflict` when a collision appears after sealing, and the insert guard tests only the stable key. A row that exists under the stable key is skipped and recorded as SKIP in the ledger; a row that exists under any other unique key is a conflict.

### 5. A run is verified against the plan before it is Succeeded

After the final batch commits, the worker verifies the run against the sealed content and moves the job to VerificationFailed when any check fails, with the reason:

- The manifest holds exactly as many keys per table as sealing counted; fewer means rows were never read.
- Every staged row of the run is in the target now, whatever the ledger says about it.
- Every staged row that carries a value in every column of an outbound foreign key resolves in the target. Child and parent columns are paired by the key's own definition, never by the parent's catalog order. A NULL reference has no parent to resolve, so a parent table the run had no references into is not consulted at all.
- Under StrictExact, the keys the target recorded for the run's writes equal the manifest (ADR 0002).

Verification is bounded to the run's staged rows and never scans the target.

### 6. The transfer reads only the planned set

Sealing stages the discovered keys in DataPitcher-owned tables in the source and marks the included ones; the transfer reads only marked keys. Sealing resets those tables first, so a plan sealed again starts from a clean slate instead of finding its own earlier keys already discovered. The owned tables are excluded from the schema snapshot hash so that sealing does not change the source schema it depends on.

### 7. Sealing uses the selection's own schema snapshot

Sealing always validates against the snapshot the selection was saved with. It never substitutes a newer scan. When the source schema no longer matches that snapshot, sealing refuses and tells the operator to scan the source, open the selection in the workbench, choose the current snapshot under "Schema snapshot", save, and seal again.

### 8. The column mapping is explicit, prefilled, and checked before sealing

Every reachable table's columns are mapped to the target by name, without regard to case, and the operator can change any of them on the plan page: a different target column, a different target table, or no transfer at all. The choices are stored on the plan as overrides; everything not overridden keeps its default, so a plan with no choices behaves exactly as before. The mapping is reviewed from the selection's own schema snapshot and the target's latest snapshot, and every problem the target would raise is shown before sealing and repeated as a refusal (`mapping_invalid`) at sealing:

| Problem | Severity |
| --- | --- |
| A stable-key or followed foreign-key column has no target | blocker |
| An override names a target column the table does not have | blocker |
| Two source columns aim at one target column | blocker |
| A source column has no target of the same name (its values are dropped) | warning |
| A NOT NULL target column receives no source column | warning |
| Types differ, or a nullable source feeds a NOT NULL target | warning |
| The target snapshot lacks the table or is missing altogether (mapping unchecked) | warning |

The transfer writes only the mapped target columns and lets the target fill the rest; a unique key over an unmapped column is left to the target to enforce. Seal-time collision checks and verification read source columns through the mapping.

## Consequences

An operator can trust that a Succeeded job moved the planned set and nothing else, and that a plan which could not be proven was refused with a reason rather than approximated. The cost is that more situations refuse: a running job blocks re-sealing, a collision blocks a plan that previously would have skipped a row, and a stale plan must be sealed again after upgrading.

## Verification

The twelve-table synthetic fixture (`TwelveTableGraph` in both provider test projects) reproduces the incident shape. Provider end-to-end tests cover stale-plan refusal, invisible parents, source orphans, collisions at seal and write time, deferred columns, levelled self-references, re-sealing after a new source row and after a deleted target row, and verification of counts, presence and foreign keys, including composite keys declared out of catalog order and all-NULL references to an absent parent.
