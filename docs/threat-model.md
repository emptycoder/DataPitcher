# DataPitcher Design-Phase Threat Model

DataPitcher is an administrative tool that transfers an exact subset of rows between relational databases, initially SQL Server and PostgreSQL. It is high privilege by nature: it holds credentials or credential references for two databases, executes operator-authored SQL against a source, and writes to a target. Its React SPA authenticates through Microsoft Entra ID or generic OpenID Connect. The trust boundaries are browser to API, API to each database, API to the identity provider, API to its own control database, and the operator, who is trusted but must be constrained and audited.

This is a **design-phase threat model**. It covers threats whose mitigations change the architecture. Threats whose mitigation is purely an implementation detail are listed but marked as deferred to implementation review, because documenting a mitigation for code that does not exist yet is fiction. Update this document when the code lands.

## ASSETS

- Source data confidentiality.
- Target data integrity.
- Database credentials and secret references.
- Identity tokens and authorization codes.
- The transfer plan's integrity as an approval artifact.
- Audit records.
- The availability of both databases.

## Query and data-access threats

### T1 — Arbitrary SELECT execution through Raw SQL mode
**THREAT:** Raw SQL invokes non-read commands or provider-specific batch behavior.

**IMPACT:** Source or staging data could be modified outside the transfer process.

**MITIGATION:** The primary security boundary is a source principal with read-only access to business schemas and write access only to the DataPitcher staging schema. A provider-aware parser rejects data-modifying statements, multiple statements, and batch separators as a secondary defence; parsing is explicitly not the primary boundary. Regular expressions must never be the sole safety mechanism. Raw SQL is gated behind a dedicated permission.

**STATUS:** Architectural — decided.

### T2 — SQL injection through operator-supplied VALUES
**THREAT:** Operator-entered values alter generated SQL syntax or meaning.

**IMPACT:** A selection can change or affect database state.

**MITIGATION:** All operator values are typed parameters and are never inlined into generated SQL. The visual builder produces a typed query abstract syntax tree and never concatenates strings.

**STATUS:** Architectural — decided.

### T3 — SQL IDENTIFIER injection
**THREAT:** Table, column, schema, or staging object names alter generated SQL.

**IMPACT:** Queries can reach unintended objects or escape staging.

**MITIGATION:** Use a dedicated identifier-quoting abstraction per provider. Staging object physical names are generated rather than operator-supplied and are mapped logically in the control database.

**STATUS:** Architectural — decided.

### T4 — Denial of service through an expensive query
**THREAT:** A costly query exhausts database capacity or ties up application workers.

**IMPACT:** Activity can impair either database's availability.

**MITIGATION:** Apply separate bounded timeouts for validation, preview, and count. Propagate cancellation to every database operation. Enforce a maximum preview size server-side rather than trusting an operator-authored row limit.

**STATUS:** Architectural — decided.

### T5 — Denial of service through an enormous dependency closure
**THREAT:** Dependencies expand a selection into an unbounded transfer set.

**IMPACT:** Planning or transfer can consume excessive resources.

**MITIGATION:** Configured maximum row and estimated-byte thresholds block plan sealing. The closure is bounded by generation and reports its size before any write.

**STATUS:** Architectural — decided.

### T6 — Staging schema abuse
**THREAT:** DataPitcher's source principal can write to its staging schema.

**IMPACT:** Writable staging objects could be guessed, interfered with, or left behind.

**MITIGATION:** Use unpredictable physical names and a TTL cleanup worker that never removes objects owned by an active job. Confine staging to one dedicated schema so its blast radius is bounded and auditable.

**STATUS:** Architectural — decided.

## Identity and authorization threats

### T7 — Incorrect issuer or audience acceptance
**THREAT:** The API accepts a token from an untrusted issuer or for another audience.

**IMPACT:** An attacker could authenticate as a valid-looking principal.

**MITIGATION:** Delegate validation to the maintained provider library rather than handwritten token parsing; use no custom JWT cryptography.

**STATUS:** Architectural — decided.

### T8 — Cross-tenant token acceptance
**THREAT:** Tokens from an unintended tenant gain access.

**IMPACT:** Identities outside the intended organization could be authorized.

**MITIGATION:** Use single-tenant mode by default with an explicit tenant identifier. Multi-tenant mode is disabled by default and requires an explicit tenant allowlist. The allowlist check is added to, not substituted for, library issuer validation.

**STATUS:** Architectural — decided.

### T9 — Mutable-claim identity confusion
**THREAT:** An email address, display name, or user principal name is reassigned to another person.

**IMPACT:** Authorization can transfer to the new holder of a mutable claim.

**MITIGATION:** The authorization identity key is a tuple of provider instance, validated issuer, tenant, principal kind, and immutable subject. Presentation claims are never authorization keys.

**STATUS:** Architectural — decided.

### T10 — Group overage causing an authorization error
**THREAT:** An incomplete group claim is read as complete membership.

**IMPACT:** Access can be decided incorrectly.

**MITIGATION:** Detect the overage indicator explicitly. Never treat missing group claims as an empty set before checking for overage. Unresolved membership that could have granted access returns an indeterminate diagnostic rather than a denial or grant. An optional directory-backed resolver returns complete membership or indeterminate, never a partial or stale set.

**STATUS:** Architectural — decided.

### T11 — Attacker-influenced group-source endpoint
**THREAT:** The API follows a token claim-source endpoint.

**IMPACT:** It could make attacker-directed requests or trust arbitrary membership data.

**MITIGATION:** Never dereference the endpoint in a token claim-source. Construct any directory request only from validated tenant and object identifiers.

**STATUS:** Architectural — decided.

### T12 — Role-mapping privilege escalation
**THREAT:** Mutable group labels or bootstrap identities confer elevated permissions.

**IMPACT:** An unintended user can become an administrator or retain access.

**MITIGATION:** Group mapping uses immutable group object identifiers, never display names. Administrator bootstrap comes from a configured application role or immutable group identifier, never an email address or domain. The effective permission set is a union of positive grants gated by an absolute deny, so removing a grant can only shrink access.

**STATUS:** Architectural — decided.

### T13 — Development authentication reachable in Production
**THREAT:** Development authentication ships to production.

**IMPACT:** Environment-variable control could bypass production authentication.

**MITIGATION:** Exclude the development provider assembly from the production publish artifact with a CI assertion, in addition to a runtime environment guard.

**STATUS:** Architectural — decided.

### T14 — Protected endpoint accidentally left anonymous
**THREAT:** A routed endpoint lacks authorization.

**IMPACT:** Unauthenticated callers access sensitive data or operations.

**MITIGATION:** Use an authenticated fallback authorization policy. An automated test enumerates every routed endpoint and requires either authorization metadata or an allow-anonymous marker with explicit written justification—never neither and never both.

**STATUS:** Architectural — decided.

## Token and session threats

### T15 — Access token leakage through a Server-Sent Events query string
**THREAT:** A token appears in an SSE URL.

**IMPACT:** It can leak through URLs, logs, telemetry, browser history, or intermediaries.

**MITIGATION:** Use a fetch-based SSE client that carries the token in an Authorization header. Tokens are forbidden in URLs, query strings, logs, persisted client state, error telemetry, and browser history.

**STATUS:** Architectural — decided.

### T16 — Token persistence in browser storage
**THREAT:** Tokens persist in browser storage beyond the active session.

**IMPACT:** Storage access enables token reuse.

**MITIGATION:** Hold tokens in memory within the authentication provider's closure. Persisted client state uses an explicit allowlist so omitting a filter cannot accidentally persist everything.

**STATUS:** Architectural — decided.

### T17 — Authorization code interception, login state attacks, and open redirect
**THREAT:** The sign-in flow is intercepted, replayed, or attacker-redirected.

**IMPACT:** An attacker can obtain a session or authorization response.

**MITIGATION:** Use authorization code flow with PKCE, state, and nonce validation. Do not use implicit flow or resource owner password credentials flow. Restrict redirect targets to a configured allowlist.

**STATUS:** Architectural — decided.

### T18 — Stale authorization
**THREAT:** An operator continues to act under a token after permissions are revoked.

**IMPACT:** A revoked user could observe updates or transfer before token expiry.

**MITIGATION:** The SSE endpoint re-authorizes the specific job resource on every stream open, not only at first connection, and closes the stream at token expiry to force revalidation. Every protected action is authorized server-side at execution time regardless of client belief.

**STATUS:** Architectural — decided.

### T19 — Cross-origin misconfiguration
**THREAT:** Cross-origin policy admits an untrusted origin.

**IMPACT:** Browser requests or responses may reach an unintended origin.

**MITIGATION:** Use a strict CORS allowlist, HTTPS, and secure headers.

**STATUS:** Deferred to implementation review.

## Secret handling threats

### T20 — Database secret leakage
**THREAT:** Database secrets appear in responses, logs, telemetry, examples, or errors.

**IMPACT:** Access to those channels can permit database connections.

**MITIGATION:** Store secret references rather than plaintext secrets. Passwords, tokens, client secrets, full connection strings, and secret-reference contents are never returned or logged. Database secrets never reach client state or the frontend bundle.

**STATUS:** Architectural — decided.

### T21 — Secret leakage into the audit log
**THREAT:** Audit records capture sensitive claims, values, or parameters.

**IMPACT:** Audit becomes a secondary confidential-data source.

**MITIGATION:** Audit records store the effective role and permission set used for an operation, not raw token claims. Row values and raw SQL parameter values are not logged by default.

**STATUS:** Architectural — decided.

## Data integrity threats

### T22 — Writing rows outside the planned manifest
**THREAT:** Transfer payloads include rows outside the sealed plan.

**IMPACT:** The target receives unapproved data.

**MITIGATION:** Every payload read joins the sealed manifest. Perform mandatory post-transfer verification. A job reaches Succeeded only after verification passes.

**STATUS:** Architectural — decided.

### T23 — Trigger side effects producing rows outside the manifest
**THREAT:** Target server-side writes create unplanned rows.

**IMPACT:** Strict verification could assert an unprovable exact result.

**MITIGATION:** Block strict verification mode when any trigger, rewrite rule, or cascading server-side write path exists on a planned target table. Trigger presence is detectable, but trigger effects are not provable.

**STATUS:** Architectural — decided.

### T24 — Constraint manipulation left in place
**THREAT:** A crash leaves target constraints disabled or untrusted.

**IMPACT:** Later writes can violate integrity or rely on a target whose guarantees were weakened.

**MITIGATION:** Write a target-local mutation journal in the same transaction as the mutation. Recovery detects and repairs before any transfer. An unrepairable mutation quarantines the table until an operator clears it and is never auto-cleared.

**STATUS:** Architectural — decided.

### T25 — Recovery state corruption and replay ambiguity
**THREAT:** A worker cannot determine whether a batch committed, or a stale worker commits after replacement.

**IMPACT:** Rows can be applied twice, omitted, or applied by competing workers.

**MITIGATION:** Keep the authoritative checkpoint in the target inside the apply transaction, so a batch committed if and only if the checkpoint advanced. Assert a fencing token inside the apply transaction to prevent a stale worker's commit.

**STATUS:** Architectural — decided.

### T26 — Cross-provider type corruption
**THREAT:** Values change meaning or cannot be represented across SQL Server and PostgreSQL.

**IMPACT:** Target data can lose precision, semantics, or completeness.

**MITIGATION:** Classify mapping status. Potentially lossy and unsupported conversions block plan sealing by default and are overridable only through a dedicated permission.

**STATUS:** Architectural — decided.

### T27 — Plan tampering or stale-plan execution
**THREAT:** A sealed plan executes after the conditions it approved materially changed.

**IMPACT:** The approval artifact no longer describes the transfer actually performed.

**MITIGATION:** Use a canonical plan hash. Any material change to connections, database identity, schema snapshot, selections, parameters, stable key definitions, relationship or conflict policies, column mappings, transfer mode, consistency mode, or trigger and constraint strategy invalidates the seal and forces a new plan version. Detect drift and block rather than silently adapting.

**STATUS:** Architectural — decided.

### T28 — Replay and idempotency abuse
**THREAT:** A duplicated start request creates two transfers.

**IMPACT:** The target may receive duplicate or competing writes.

**MITIGATION:** Require an idempotency key for start and enforce single-owner job semantics with the fencing token.

**STATUS:** Architectural — decided.

## Operational threats

### T29 — Trusted operator exfiltration through preview or export
**THREAT:** An operator with legitimate access uses preview or export to exfiltrate source data.

**IMPACT:** DataPitcher cannot prevent this because the operator is trusted by design. The residual risk is that an authorized operator can disclose data they are permitted to read.

**MITIGATION:** These are compensating rather than preventive controls: least-privilege source principals, allowed-schema configuration, bounded preview size, and audit records of who previewed and transferred what.

**STATUS:** Architectural — decided. Residual risk accepted.

### T30 — Elevated operations performed without adequate scrutiny
**THREAT:** Constraint suspension, trigger override, or lossy mapping is used without specific approval or traceability.

**IMPACT:** A transfer can weaken integrity safeguards or knowingly alter data without a clear accountable record.

**MITIGATION:** Gate each operation behind its own dedicated permission rather than a general administrator role. Each requires an explicit operator action and produces an audit record.

**STATUS:** Architectural — decided.

## RESIDUAL RISKS

This design does not protect against a malicious trusted operator exfiltrating data they are authorized to read. It does not protect the exact-set guarantee from concurrent third-party writes to the target during a transfer; that guarantee explicitly does not cover those writes. It does not protect against compromise of the host running the API, which holds database credentials. It also does not prevent value divergence in a target-satisfied dependency row: that is a deliberate product decision, not a defect.
