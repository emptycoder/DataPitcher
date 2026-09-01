# ADR 0004: Single Demand-Driven, Target-Aware Closure

## Title

DataPitcher computes one demand-driven closure that consults target state during expansion.

## Status

Accepted; supersedes the specification's two-phase closure.

## Date

2026-09-01.

## Context

DataPitcher transfers an exact, minimal, referentially complete subset of rows from a source relational database to a SQL Server or PostgreSQL target. A foreign key `child.col -> parent.col` is a directed edge from child to parent. Default traversal follows outgoing edges: selecting Orders pulls in referenced Customers, while selecting Customers does not pull in Orders. An operator may explicitly enable selected inbound, reverse relationships.

Roots have one of three conflict policies. FailOnConflict treats an existing target root as a blocker. SkipExisting neither writes an existing root nor expands its dependencies. Upsert writes an existing root as an update and expands its dependencies.

The original product specification mandated four phases: seed roots; compute a full source-only CANDIDATE closure; bulk-probe the target to classify those candidates; then run a second, minimal closure from write-eligible roots. It justified this by saying that “a candidate closure followed only by write-time skipping is insufficient.” This ADR explicitly supersedes that design.

## Decision

### 1. Use one demand-driven, target-aware closure

Delete the candidate phase. The original justification conflated two claims. It correctly rejects deferring conflict decisions to write time, because that lets rows expand dependencies before knowing whether they are eligible. It does not follow that two closures are necessary. A single closure that consults target state during expansion makes the conflict decision before expansion and is therefore correct without a candidate closure.

The closure is a breadth-first fixed point over generations:

1. Seed generation 0 with root stable keys only after all root selection set algebra—UNION, INTERSECT, and EXCEPT—has been fully applied.
2. Batch-probe the current generation's keys against the target in one set-based operation. Never issue one query per key or use large IN lists.
3. For generation 0, apply the root conflict policy to determine write eligibility. For later generations, apply the dependency satisfaction rule in Decision 3.
4. Expand only included rows along enabled relationships, set-based inside the source database, to produce required parent keys.
5. Deduplicate newly required keys against all staged keys, then insert only genuinely new keys with generation G+1.
6. Apply a barrier and repeat until a generation produces no new keys.

This deletes the candidate key set, the persisted complete edge relation, the second propagation engine, and the specification property “FinalManifest is a subset of CandidateSet.” That property is meaningless without a candidate set. The original specification's property-based test list must be revised accordingly.

The cost is more source-to-target round trips: one target probe per generation rather than one overall probe. Generations are bounded by dependency-graph depth and are typically fewer than ten. The visible candidate set previously served as an audit artifact; it is lost. The explicit record of TargetSatisfied rows replaces that audit function.

### 2. Reject candidate closure on sizing evidence

The superseded design depended for correctness on persisting the complete deduplicated relation `(dependent_row, required_row, relationship_id)`. Keeping only a first-discovery edge per row makes demand arriving through another path invisible and silently under-includes rows. The required relation is O(candidate rows × enabled relationships).

A realistic large case with 50 million candidate child rows and six populated enabled relationships produces roughly 300 million edges. At approximately 50 to 150 bytes per edge including indexes, source staging alone requires 15 to 45 GB, and wide composite keys increase it further. This storage requirement is a primary reason the two-phase design was rejected.

### 3. Define target satisfaction and its guarantee

DataPitcher's product guarantee is target referential integrity, not source-graph fidelity. A required parent is TargetSatisfied, and is neither transferred nor expanded, if and only if both conditions hold: its referenced key resolves in the target, and the corresponding target foreign key exists, is enforced, and is trusted. For SQL Server, trusted means `is_not_trusted = 0`; for PostgreSQL, it means `convalidated = true`.

An absent, disabled, untrusted, or unvalidated constraint means the row is not satisfied. DataPitcher transfers it and continues expanding through it. This conservative rule is always sound and is simpler than probing the target row's own parents. Planning emits a warning that names each untrusted constraint so the operator knows why the transfer grew.

Trust is required because pruning a satisfied parent's ancestors depends on induction: if P exists in the target, then P's required parents exist there. Only an enforced, trusted target constraint licenses that induction.

This guarantee does not refresh values. A TargetSatisfied parent can have different non-key values from its source parent, and DataPitcher will not update it. Minimality and value refresh are incompatible goals. An operator requiring refreshed values must include that table with an Upsert policy. Under Upsert semantics for a dependency, “existing” and “satisfied” are contradictory: the row is rewritten from source values, so it is included and expanded.

Satisfaction belongs to the pair `(row, referenced unique key)`, not to a row's mere existence. A target foreign key can reference a unique constraint rather than the primary key, and one row can satisfy one referenced key while diverging on another.

### 4. Track frontiers with a strict barrier

A mutable `is_expanded` flag is unsafe. A blanket UPDATE after a predicate-based frontier SELECT can mark rows inserted concurrently by another relationship expansion but never read. Those rows are then permanently omitted from expansion, causing silent under-closure.

An immutable generation stamp alone merely moves the race. If key K is first stamped generation 5 after a worker has already selected generation 5, K cannot be restamped and may never be processed. Use immutable generation stamps, set only on first insertion and preserved on rediscovery through conflict-ignoring insertion, plus a strict generation barrier: every expansion producing generation G must finish before any expansion consumes generation G+1.

The rejected alternative is a durable per-row-per-relationship work queue with atomic claim and acknowledge plus explicit quiescence detection. It is correct, but is more machinery than a single-worker analysis phase requires.

### 5. Freeze closure preconditions

The fixed point is well-defined only with these hard preconditions: one frozen source snapshot; finalized root conflict policies; a global, immutable, path-independent enabled-relationship set; completed selection set algebra before seeding; consistent NULL semantics; and satisfaction results that do not change mid-closure. The latter requires target snapshot stability or explicit acceptance that satisfaction is evaluated as of each probe.

A live inbound traversal against a source receiving continuous inserts is not guaranteed to terminate. The frozen source snapshot is therefore a correctness precondition, not an optimization.

### 6. Rely on monotonicity for termination

The closure monotonically accumulates over a finite domain, a subset of source rows. A unique index over the complete stable key makes insertion idempotent. Each generation either adds at least one previously absent key or halts. Cycles do not matter because termination concerns the key set, not the path set: a self-referencing chain stops when its parent key is staged; a two-table cycle re-derives staged keys and inserts nothing; and the second discovery in a diamond is a no-op. The worst-case generation count is the longest simple dependency chain.

No rule retracts inclusion. The operator is monotone. A design that un-included rows after later finding them satisfied would be non-monotone and could oscillate.

### 7. Persist bounded provenance

For every included row, persist stable key columns, immutable discovery generation, first-discovery relationship, predecessor key, and a link to its originating root selection. Generation strictly decreases along predecessor links. Following them therefore yields exactly one acyclic, terminating, shortest human explanation of inclusion.

Also persist negative provenance: TargetSatisfied, RootSkipped, or NotDemanded. Without it, operators can investigate completeness complaints but cannot investigate minimality complaints.

Provenance deliberately cannot report how many distinct ways a row was required, enumerate all justifying paths, or answer whether the row would disappear if root selection S were removed. Path enumeration is exponential; the counterfactual requires rerunning closure without S.

## Consequences

Planning no longer materializes a source-only candidate universe or its complete demand edge relation. It instead stages included keys and explicit exclusions generation by generation, probes target state in bounded set-based batches, and exposes why target state pruned demand. The system accepts additional target round trips in exchange for eliminating substantial staging storage, a propagation engine, and a multi-path correctness hazard.

Target integrity is the asserted outcome. Source-value equivalence for target-satisfied dependencies is intentionally outside that outcome. Operators needing it select Upsert and accept the resulting expanded transfer. Planning must surface untrusted target constraints and the as-of-probe scope when target stability is unavailable.

## Alternatives Considered

The original two-phase candidate design was rejected. Computing all source candidates before target knowledge postpones eligibility information and needs a second closure to avoid write-time skipping. Its correctness also requires the complete deduplicated dependent-to-required edge relation, whose large-case staging cost is untenable. Retaining only a first-discovery edge is not an acceptable optimization because it loses multi-path demand. A target-aware decision at the only expansion point gives the required correctness with one monotone propagation engine.

The durable per-row-per-relationship work queue was also rejected for this phase. Its atomic claims and quiescence detection solve concurrent frontier processing, but a strict generation barrier supplies the needed safety for a single worker with materially less machinery.

## Verification

Test generation-zero policies: FailOnConflict blocks, SkipExisting writes no root and contributes exactly zero dependencies, and Upsert writes and expands an existing root. Test that root UNION, INTERSECT, and EXCEPT finish before seeding, probes are set-based batches, and only included rows expand.

Use a diamond fixture where a root depends on A and B, and both depend on X. Make A present in the target and B missing; assert that X is still transferred through B. This guards against first-discovery-edge loss and against treating target existence as universal satisfaction.

Use an untrusted-constraint fixture: although the referenced parent key resolves in the target, an absent, disabled, untrusted, or unvalidated matching target foreign key must cause transfer and continued expansion, with a plan-time warning naming the constraint. Verify trusted SQL Server and validated PostgreSQL constraints permit TargetSatisfied pruning, including foreign keys to non-primary unique keys.

Test strict barriers by arranging concurrent relationship expansions that discover a later-generation key while another expansion is active; verify the key is consumed in its stamped generation. Test self cycles, two-table cycles, and diamonds terminate through idempotent stable-key insertion. Verify provenance yields one shortest acyclic predecessor chain and records TargetSatisfied, RootSkipped, and NotDemanded exclusions. Test source snapshot preconditions and document the as-of-probe behavior when target snapshot stability is not provided.

## Open Questions

None.
