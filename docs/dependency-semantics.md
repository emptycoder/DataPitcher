# Dependency Semantics

This is the normative reference for what DataPitcher transfers, and why. It defines the invariant every transfer strategy, closure implementation, and verification step must satisfy. Where behaviour is ambiguous, this document — not any implementation — is authoritative.

## 1. The central invariant

DataPitcher transfers the smallest correct relational dataset. Formally: the final transfer set is the union of (root rows the selected conflict policy will actually write) and (the smallest transitive set of rows required by enabled relationships that are not already satisfactorily present in the target). No row outside that set may be inserted or updated by DataPitcher.

This is non-negotiable. Every transfer strategy must prove it holds through tests and post-transfer verification — not merely assert it by construction.

Explicit anti-patterns, forbidden regardless of convenience or performance:

- Never copy a whole table merely because it appears in the dependency graph. Appearing in the graph means a relationship *could* pull rows from that table; it does not license copying the table wholesale.
- Never read an entire source table into application memory and filter there. Filtering is a database operation, not an in-process one.
- Never transfer every row of a dependency table because some rows of it are required. Only the specific required rows are transferred.
- Never add unplanned rows during execution. Everything transferred traces back to plan-time selection and closure logic. A plan change always produces a new plan version; execution never silently deviates from the plan it was given.

## 2. Edge direction, stated unambiguously

A foreign key on child.column referencing parent.column produces a directed edge child -> parent, read as "the child depends on the parent." Worked example: sales.orders.customer_id references sales.customers.id, producing the edge orders -> customers, because an order depends on a customer.

Default traversal follows outgoing edges from selected rows. Therefore selecting Orders may pull in referenced Customers; selecting Customers must not pull in all Orders. Inbound dependants are included only when an operator explicitly enables that specific reverse relationship — for example, enabling orders -> order_lines as an inbound inclusion rule. Enabling one inbound relationship does not implicitly enable any other.

Tables used only in SQL joins do not become root transfer tables; joins affect which stable keys are selected for the declared root table, and nothing more. Concrete example: selecting pending orders — joining orders to a status table purely to filter — automatically includes their missing referenced customers (an outgoing dependency of the root), but does not automatically include their order lines (an inbound dependant, not enabled by default).

## 3. Stable row identity

Every transferable table needs a reliable stable row identity, selected in this priority order: primary key; then a non-null unique constraint explicitly chosen by the operator; then an explicitly configured virtual stable key that DataPitcher has proven unique for the selected data and target mode.

A table with no reliable stable identity is marked Blocked and cannot participate in dependency resolution, target-existence probing, deduplication, conflict detection, resumability, upsert, or exact-set verification. Each requires identity for a distinct reason:

- Dependency resolution: names which parent row a foreign key value refers to.
- Target-existence probing: asks "does this specific row already exist," not "does some row with these values exist."
- Deduplication: recognizes that two discovery paths reached the same row.
- Conflict detection: distinguishes "this row already exists" from "a different row has colliding non-key values."
- Resumability: lets a resumed run determine which rows a prior attempt already wrote.
- Upsert: targets the correct existing row for update rather than inserting a duplicate.
- Exact-set verification: compares rows actually written against the set the plan intended.

Composite keys must work throughout every one of those operations. Preserve key column order, native database types, provider equality semantics, collation implications, and null behaviour — a composite key is not reducible to a concatenated string without risking false or missed matches under provider-specific comparison rules. Primary key and required foreign key columns can never be removed from the transfer mapping, even when an operator's column selection would otherwise exclude them, because doing so would silently break identity or referential resolution downstream. Keys must not be serialized into a universal JSON value for database joins; staging uses typed key columns matching the source types, so joins and comparisons execute with native provider semantics rather than string equality.

### CLR type handling in stable keys

`StableKey` does not normalize CLR types. Key components must be materialized by the provider adapter using the CLR type declared in the schema snapshot so that same-provider transfers stay consistent by construction. Cross-provider key-type divergence is therefore a type-mapping compatibility issue, surfaced through existing `SafeWidening` and `ExplicitConversionRequired` status classes, and cross-provider transfer is blocked by default until a compatibility matrix is in place. A test guard is required when constructing stable keys from schema to assert every key component CLR type matches its declared column type.

## 4. The closure algorithm

The closure is a single, demand-driven, target-aware breadth-first fixed point computed over generations. An earlier design used two closures — a source-only candidate closure followed by a separate minimal final closure — superseded by this design. See ADR 0004 for the reasoning.

1. Seed generation zero with root stable keys, after all selection set-algebra (filters, joins, unions, exclusions) has been fully applied — generation zero is the *output* of set-algebra, not exempt from it.
2. Batch-probe the current generation's keys against the target in a single set-based operation. Never one query per key; never a large IN list.
3. Determine write-eligibility per row: generation zero uses the root conflict policy (section 6); later generations use the target-satisfaction rule (section 5).
4. Expand only INCLUDED rows, following enabled relationships, set-based inside the source database, yielding required parent keys.
5. Deduplicate the newly yielded keys against every key already staged, insert only the genuinely new ones, stamping each with generation G+1.
6. Barrier: wait for step 5 to complete for the whole generation. Repeat from step 2. Terminate when a generation adds nothing new.

Frontier discipline: generation stamps are immutable — set once, at first insert, preserved on later re-discovery via conflict-ignoring insertion (a no-op on duplicate keys rather than an overwrite). A mutable "expanded" flag is forbidden: a blanket update after a predicate-based frontier read can mark concurrently-inserted rows as expanded even though they were never read, causing permanent silent under-closure. A generation stamp alone does not close this gap either — it relocates the race to "did generation G's expansion see this key before or after it was stamped G" — so a strict generation barrier is also required: every expansion producing generation G completes before any expansion consuming generation G+1 begins.

Termination argument: monotone accumulation over a finite domain (the source database's rows), with idempotent insertion enforced by a unique index over the complete stable key. Each generation either adds a previously-absent key or the fixed point is reached and the algorithm halts. Cycles are irrelevant because termination is a property of the *key set*, not the *path set*. A self-reference re-discovers its own starting key, contributing nothing after the first insertion. A two-table cycle alternates between two finite key sets, halting once both are exhausted. A diamond causes a row to be discovered twice, but deduplication in step 5 collapses both into one stamped generation.

Frozen preconditions: one frozen source snapshot for the duration of the closure; finalized conflict policies before seeding; a global, immutable, path-independent enabled-relationship set; all selection set-algebra completed before seeding generation zero; consistent null semantics. A live inbound traversal over a continuously-inserting source is not guaranteed to terminate — this is why the frozen snapshot is a hard precondition, not an optimization.

## 5. Target satisfaction — the rule and its guarantee

DataPitcher guarantees target referential integrity, not source-graph fidelity. It ensures whatever ends up in the target has all required foreign keys resolvable within the target; it does not guarantee the target's dependency graph mirrors the source's graph shape or values beyond what integrity required.

A required parent row is TargetSatisfied — not transferred, not expanded — if and only if both hold: the referenced key resolves to an existing target row, and the corresponding target foreign key constraint exists, is enforced, and is trusted (not-trusted flag clear, in SQL Server terms; validated flag set, in PostgreSQL terms).

If the constraint is absent, disabled, untrusted, or unvalidated, the row must be treated as not satisfied: it is transferred, and expansion continues through it. This is deliberately conservative — always sound, and simpler than probing the target's own parent chain to determine whether its existing row is internally consistent. A plan-time warning must name every untrusted or unvalidated constraint encountered, so the operator understands why the transfer grew.

The induction being licensed here is precise: pruning a satisfied parent's ancestors is the claim "P exists in the target, therefore P's required parents also exist in the target." That claim is only true if an enforced, trusted constraint is what guarantees P's own referential integrity within the target. Without that guarantee the claim is unfounded, which is exactly why an absent or untrusted constraint forces continued expansion.

An honest limitation, stated prominently: a TargetSatisfied parent may hold different non-key column values than its source counterpart, and DataPitcher will not refresh them. Minimality and value refresh are incompatible goals — refreshing values requires writing to rows that satisfaction logic exists specifically to avoid writing to. An operator who needs a dependency table's values kept current must include that table with an Upsert policy rather than relying on satisfaction-driven pruning.

A further subtlety: satisfaction is a property of the pair (row, referenced unique key), not mere existence in the target, because a foreign key may reference a unique constraint other than the primary key. A row can exist in the target and still fail to satisfy a specific reference if the key combination the foreign key targets does not match.

## 6. Conflict policies and their effect on the closure

Each policy has both a write behaviour and an expansion behaviour; expansion determines the closure's minimality, and the two must be considered together.

**FailOnConflict** (the default). Write behaviour: if a planned root row already exists in the target, this is a blocker — the plan does not seal, and the run does not proceed silently past it. Sanitized, key-based conflict samples are returned so the operator can see which rows conflicted without leaking unrelated column data. Expansion behaviour: existing dependency rows (as opposed to root rows) may still satisfy references when configured to, per section 5.

**SkipExisting**. Write behaviour: an existing root row is not written. Expansion behaviour: it also does not expand its dependencies — a skipped root contributes exactly zero dependency rows. Missing root rows are inserted and expanded normally. Verification after transfer must confirm that skipped rows do not leave any required reference unresolved in the target.

**Upsert**. Write behaviour: an existing root row is written as an update. Expansion behaviour: it does expand its dependencies, because the source's foreign key values are about to be applied to the target row, and those values must resolve. Upsert must never update stable-key columns, generated or computed columns, or row-version columns. Record the deliberate contradiction here: under Upsert, "existing" and "satisfied" are mutually exclusive for a dependency row. It is about to be rewritten with source values regardless of what currently exists, so it must be included and its own dependencies expanded — existence in the target does not exempt it.

Destructive replace-by-delete semantics are not implemented.

## 7. Provenance — answering "why was this row included?"

For every included row, DataPitcher persists: the stable key columns, the immutable discovery generation, the relationship first discovered through, the predecessor key discovered from, and a link to the originating root selection.

Because generation strictly decreases along the predecessor chain back toward generation zero, following that chain yields exactly one acyclic, terminating, shortest explanation a human can read. Worked example chain: a root selection of pending orders includes order 4471 at generation zero; order 4471's customer foreign key discovers customer 892 at generation one, predecessor order 4471; customer 892's region foreign key discovers region "EMEA" at generation two, predecessor customer 892. That three-hop chain is the complete answer to "why is region EMEA in this transfer."

Negative provenance is persisted as well: an exclusion reason of TargetSatisfied, RootSkipped, or NotDemanded, recorded for rows that were considered but not transferred. Without it, operators can debug completeness complaints ("why isn't this row here") but not minimality complaints ("why did this unrelated row show up").

Provenance deliberately cannot answer three questions: how many distinct ways a row was required, since only the first discovery is recorded; the enumeration of all justifying paths to a row, since that is exponential in general graphs and deliberately not computed; and whether a row would disappear if a particular root selection were dropped, since that requires re-running the closure without that selection — provenance records what happened, not a counterfactual.

## 8. Relationship cases that must work

- **Composite keys**: compared together as a unit, in constraint-native order, using provider-native equality.
- **Composite foreign keys**: all referencing columns must be non-null together for the relationship to be followed; the whole key resolves as one unit.
- **Nullable foreign keys**: a null optional foreign key adds no parent — there is no reference to satisfy.
- **Foreign keys referencing a unique constraint**: resolution uses the specific referenced unique key, not the primary key, per section 5.
- **Self references**: a table referencing its own primary key (e.g. an employee's manager) is followed and terminates once no new self-referenced key is discovered.
- **Self-reference write order**: ancestors are written before descendants. Sealing levels the rows by mapping the referenced columns to the stable key through the table itself, so a self-reference onto any unique key is levellable, not only one onto the primary key; a nullable self-reference that is not levelled is filled in after every row exists (ADR 0008 §2).
- **Multiple distinct foreign keys between the same pair of tables**: each tracked and followed as its own relationship, remaining distinct in provenance and configuration, never merged.
- **Shared dependencies**: a row required by two or more discovery paths is transferred exactly once, deduplicated per section 4 step 5.
- **Diamond dependencies**: two paths converging on the same downstream row must still result in inclusion if either upstream branch requires it.
- **Cycles**: detected and handled without infinite expansion, per the termination argument in section 4; see ADR 0003 for provider-specific cycle write-ordering.
- **Manually declared relationships**: not expressed as database foreign key constraints, but explicitly configured and participate in the closure identically to constraint-derived ones.
- **Source orphans**: a row whose foreign key value does not resolve to any parent in the source is transferred as-is; DataPitcher does not fabricate a missing parent.
- **Unique-key collisions**: a planned row whose value on a target unique key other than the stable key belongs to a different target row is never skipped, because its children would then reference the wrong parent or dangle; sealing refuses the plan and the transfer stops the run (ADR 0008 §4).
- **Disabled relationships**: contribute no rows under any circumstance, regardless of what generation or root selection would otherwise reach them.

## 9. The required behavioural test list

1. Selecting a child includes its missing parent.
2. Selecting a parent excludes inbound children by default.
3. Explicit inbound traversal includes children when enabled.
4. A null optional foreign key adds no parent.
5. A composite foreign key resolves correctly as a unit.
6. A shared parent reached via two paths is copied exactly once.
7. An existing target parent behind a trusted, enforced constraint terminates the branch.
8. Ancestors reachable only through a satisfied parent are pruned.
9. A SkipExisting root expands nothing.
10. An Upsert root expands its dependencies.
11. A self-reference hierarchy resolves without infinite expansion.
12. A genuine row-level cycle is detected and handled.
13. A table with no stable key is marked Blocked.
14. An alternative (non-primary-key) stable key works end to end.
15. Overlapping selections deduplicate to a single set.
16. A raw SQL join selects only root keys, not joined-table rows.
17. No per-key target-existence query is issued during probing.
18. A foreign key referencing a unique constraint resolves correctly.
19. Multiple foreign keys between the same two tables remain distinct.
20. A disabled relationship contributes no rows.

Three additional fixtures are the highest-value correctness tests and must exist explicitly:

**(a) DIAMOND.** Root R depends on A and B; both A and B depend on X; A is present in the target, B is missing. Assert X is still transferred — this one test catches both pruning X because one demanding parent (A) appears satisfied, and failing to deduplicate X if both branches queue it independently.

**(b) UNTRUSTED CONSTRAINT.** The target contains parent row P, and P's own required parent is missing, but the constraint enforcing that requirement is unvalidated or not-trusted. Assert DataPitcher either transfers the missing ancestor (treating P as not satisfied) or fails with an explicit diagnostic naming the untrusted constraint — never silently succeeds while leaving the target referentially broken.

**(c) CONCURRENT FRONTIER.** A fan-in table reachable via two relationships from two different generation-G rows expanded concurrently. Assert the resulting closure is identical, row for row and generation for generation, to a single-threaded reference run — verifying the generation barrier in section 4 prevents the under-closure race it is designed to prevent.

## Open Questions

- The exact virtual-stable-key uniqueness proof procedure (checks run against selected data and target mode before accepting an operator-declared virtual key) is referenced but not specified here.
- Sanitization rules for key-based conflict samples returned under FailOnConflict are not specified here.
- Provider-specific cycle write-ordering mechanics are deferred to ADR 0003 and not restated here.
