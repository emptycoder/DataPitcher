# ADR 0005: Native Bulk Writers and LINQ to DB Scope

## Title

Native provider bulk writers are required for transfer writes; LINQ to DB remains the portable query and schema baseline.

## Status

Accepted. This ADR overrides the specification's LINQ to DB bulk-copy allowance.

## Date

2026-09-01.

## Context

DataPitcher is a .NET 10 / C# application that transfers an exact planned set of rows between SQL Server and PostgreSQL. The original specification made LINQ to DB 6.x the primary relational-data library, allowed `BulkCopyAsync` only after integration proof, forbade silent row-by-row fallback, and required the actual writer strategy in plans, logs, metrics, and audit details.

The findings in this ADR are based on reading LINQ to DB 6.4 library source and official documentation during architecture analysis; no automated verification artefact exists in the repository yet. The behavioural claims that `ProviderSpecific` is advisory and silently degrades, and that the result object does not report which strategy actually ran, must be confirmed by an integration test before this decision is relied upon. The transfer write boundary must therefore use APIs that provide a native-writer contract rather than an advisory request that can silently choose another implementation.

## Decision

### 1. Exclude LINQ to DB BulkCopy from the transfer write path

Architecture analysis indicates that LINQ to DB 6.4 `BulkCopyType.ProviderSpecific` is advisory, not contractual. Its documentation permits an unsupported provider-specific request to downgrade to row-by-row copying. Source analysis indicates that the implementation first falls back to a multi-row SQL `INSERT`, and that path can itself fall back to row-by-row insertion. The SQL Server and PostgreSQL bulk-copy paths are also indicated to fall back when they cannot unwrap the concrete provider connection or transaction from a wrapping connection.

Architecture analysis indicates that `BulkCopyRowsCopied` reports only a copied-row count and abort flag, not the strategy that ran. The default `BulkCopyType` is `MultipleRows` for PostgreSQL and SQLite and `ProviderSpecific` for SQL Server. DataPitcher can neither obtain a native-or-fail guarantee nor identify the strategy after the fact, violating its degradation and recording requirements.

DataPitcher will call `Microsoft.Data.SqlClient` `SqlBulkCopy` and Npgsql's binary importer directly from provider-specific projects. LINQ to DB remains the primary library for queries, schema discovery, and the SQLite control database. SQLite has no native bulk writer in LINQ to DB: its implementation emits only multi-row `INSERT` statements. A native-bulk request for SQLite must be rejected, never silently substituted.

### 2. Augment LINQ to DB schema discovery with provider catalogs

The portable schema path is `IDataProvider.GetSchemaProvider()` then `GetSchema(...)`; LINQ to DB 6.4 has no asynchronous `GetSchemaAsync`. Primary-key metadata is on `IsPrimaryKey` and `PrimaryKeyOrder`; foreign keys expose positional `ThisColumns` and `OtherColumns` in provider ordinal order.

PostgreSQL primary-key order is not trustworthy. It derives order from physical column position (`attnum`), so a composite key is wrong whenever declaration and table-column order differ. This is a correctness defect: DataPitcher uses ordered composite stable keys as row identity, and a wrong order corrupts staging joins, target-existence probes, deduplication, and verification.

The schema API also omits unique non-primary constraints, column defaults, computed or generated-column kind, and constraint trust or validation state. `SkipOnInsert` and `SkipOnUpdate` are writeability hints, not a generated-column model. LINQ to DB is a portable baseline only; catalogs must provide the omitted metadata and composite-key order. Trust and validation state are mandatory because dependency pruning depends on them.

### 3. Apply the SQL Server writer contract

Construct `SqlBulkCopy` with the concrete connection, options, and an optional external transaction. Set destination table and explicit mappings, set `EnableStreaming` to `true`, and use the asynchronous `DbDataReader` overload with a cancellation token. Defaults are `BatchSize` `0`, `BulkCopyTimeout` 30 seconds, streaming off, and `KeepIdentity`, `CheckConstraints`, `FireTriggers`, `TableLock`, and `KeepNulls` off. `SqlBulkCopy` returns no generated keys.

DataPitcher always supplies an external transaction. `UseInternalTransaction` cannot be combined with an external transaction. Without a caller-controlled transaction, batches completed before a later failure remain committed, leaving an unbounded and unknowable partial state. An external transaction is the required all-or-nothing boundary.

### 4. Apply the PostgreSQL writer contract

Use binary `COPY FROM STDIN` through Npgsql's asynchronous binary import method. Begin every row, write values and nulls explicitly, then call asynchronous `Complete`, which returns the copied-row count and no generated values. Disposing or closing an uncompleted importer cancels and reverts its `COPY`; this required failure behavior must never result from an accidental early return. Explicit PostgreSQL types are safest for ambiguous CLR mappings. Multiple completed `COPY` operations need an external transaction for all-or-nothing behavior.

When an identity column is omitted, its default is invoked. When a value is supplied, `COPY` always writes that explicit value, including for `GENERATED ALWAYS AS IDENTITY`. `COPY` has no `RETURNING` and cannot stream generated keys. Explicit values bypass `nextval`, so an owned sequence does not advance. A preserved-key load must realign the sequence with `setval` under concurrency control. Sequence changes are immediate and are not rolled back.

### 5. Bound cancellation and timeout hazards

`SqlBulkCopy` has a separate timeout and accepts a cancellation token. The source `SqlCommand.CommandTimeout` defaults to 30 seconds and measures cumulative network-read time per invocation. Architecture analysis, not a reproduction in this repository, identified an unverified-in-repository documented open defect in Microsoft.Data.SqlClient 7.0.2: some blocked or partial-result asynchronous commands do not send the TDS attention signal, so cancellation can hang; the tracked fix targets a later major preview. Re-check the SqlClient issue tracker before this version is pinned for release.

Npgsql importer operations accept cancellation tokens and a timeout. Query cancellation waits up to the `Cancellation Timeout` setting, 2000 milliseconds by default, before terminating the attempt. Every long-running database operation must use a bounded cancellation grace period. If cancellation has not taken effect when that period expires, DataPitcher abandons and disposes the connection rather than waiting indefinitely. Cancellation tests must assert completion within that bound.

### 6. Support database authentication without storing secrets

Microsoft.Data.SqlClient supports SQL password authentication and Entra Password (deprecated), Integrated, Interactive, Service Principal, Device Code Flow, Managed Identity, Default, and Workload Identity modes. In version 7 every Entra mode requires `Microsoft.Data.SqlClient.Extensions.Azure`. Npgsql supports per-open and timer-based periodic password providers. Periodic-provider cancellation occurs only on data-source disposal, so token acquisition needs its own timeout. DataPitcher stores secret references, never plaintext.

### 7. Pin the provider compatibility set

Use .NET 10; `linq2db` 6.4.0; `Microsoft.Data.SqlClient` 7.0.2; Npgsql 10.0.3; `Microsoft.Data.SqlClient.Extensions.Azure` 7.0.2 for Entra; and `Microsoft.Data.Sqlite` 10.0.11 for control data. LINQ to DB 6.4.0 already pins SqlClient 7.0.2 and Npgsql 10.0.3. LINQ to DB and Npgsql ship direct `net10.0` assets. SqlClient ships `net9.0` but officially supports .NET 8 and later, so .NET 10 consumes that compatible asset. Select LINQ to DB's Microsoft SQLite variant, not the System.Data.SQLite variant; identity recognition is a heuristic around one `INTEGER PRIMARY KEY`.

## Consequences

Transfer code has provider-specific writers and catalog probes. This is less portable at the write boundary, but makes the writer deterministic, observable, and recordable. Native bulk is unavailable rather than emulated where no native contract exists.

Planning must account for catalog permissions and provider metadata. Composite identity order, generated-column handling, uniqueness, and enforcement state cannot be inferred from portable schema output. Direct writers require explicit transactions, mappings, cancellation escalation, and PostgreSQL sequence maintenance.

## Alternatives Considered

Retaining LINQ to DB `BulkCopyAsync` with integration tests was rejected: tests cannot turn advisory selection, connection-unwrapping failure, or unreported fallback into a native-or-fail contract. `BulkCopyRowsCopied` was rejected because its row count and abort flag do not identify the strategy.

Row-by-row and multi-row `INSERT` fallbacks were rejected because the specification requires a known writer strategy. Portable schema output was rejected as complete because it omits required metadata and can wrongly order a PostgreSQL composite key.

Using internal SQL Server bulk-copy transactions was rejected because it prevents one external transaction from fencing all batches. Relying on connection cancellation alone was rejected because reported provider-specific cancellation failures can otherwise wait indefinitely.

## Verification

Integration tests must force an unsupported `ProviderSpecific` request and a transaction-wrapped scenario. The required test must force a scenario where the provider connection cannot be unwrapped and assert the writer FAILS before any row is written rather than degrading. Verify the selected native writer is recorded in plans, logs, metrics, and audit details.

Test SQL Server mappings, streaming, a `DbDataReader`, cancellation, an external transaction, and failure after multiple batches. Verify no prior batch commits, no external transaction combines with `UseInternalTransaction`, and no generated keys are claimed.

Test PostgreSQL explicit nulls, ambiguous-type annotations, successful `Complete`, and disposal without it. Verify incomplete `COPY` reverts, multi-operation loads need an external transaction for atomicity, identity omission invokes defaults, explicit values are written, and preserved-key loads realign owned sequences under concurrency control.

Test catalog extraction with a PostgreSQL key whose declaration and column order differ; assert declaration order. Test unique constraints, defaults, generated or computed kinds, SQL Server trust, PostgreSQL validation, and dependency pruning. Test bounded cancellation for blocked or partial-result operations, including disposal after the grace period.

## Pinned Versions

.NET 10; `linq2db` 6.4.0; `Microsoft.Data.SqlClient` 7.0.2; Npgsql 10.0.3; `Microsoft.Data.SqlClient.Extensions.Azure` 7.0.2 when Entra authentication is enabled; and `Microsoft.Data.Sqlite` 10.0.11.

## Known Hazards

SqlClient 7.0.2 has an unverified-in-repository cancellation risk for blocked or partial-result asynchronous commands, identified during architecture analysis; the issue tracker must be re-checked before release pinning. Bounded abandonment and disposal mitigate, but do not repair, that limitation. Npgsql periodic password-provider cancellation requires data-source disposal and an independent token-acquisition timeout. PostgreSQL `setval` is immediate and non-transactional, so sequence realignment must exclude concurrent users. LINQ to DB PostgreSQL composite-key ordering remains unsafe until catalogs replace it.

## Open Questions

- What cancellation grace-period bound is acceptable for each supported deployment environment?
- Which catalog permissions must plan sealing require for every supported SQL Server and PostgreSQL authentication mode?
