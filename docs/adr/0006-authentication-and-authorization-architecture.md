# ADR 0006: Authentication and Authorization Architecture

## Title

DataPitcher uses provider-specific authentication at the HTTP boundary, normalized immutable identities, and permission-based authorization.

## Status

Accepted.

## Date

2026-09-01.

## Context

DataPitcher is an administrative .NET 10 ASP.NET Core Minimal API with a React 19 SPA. It moves data between production-adjacent databases and is therefore a high-privilege tool. It must support Microsoft Entra ID, generic OpenID Connect (OIDC), and a Development/Test-only provider that is impossible to enable in Production. Adding a provider must not change dependency resolution, transfer, selection, or plan business logic. Authorization is permission-based; Viewer, Planner, Operator, and Administrator are named permission bundles.

Identity, issuer validation, group completeness, service principals, and long-lived server-sent event (SSE) connections are security boundaries. Validation must be deterministic, deny uncertainty that could grant access, and leave pull-request tests independent of a real tenant.

## Decision

### 1. Retain three provider abstractions and explicit composition

Abstraction is earned only by actual variability among the required providers. Retain `IAuthProviderRegistration`, because scheme setup, configuration, and routing vary; retain `IExternalPrincipalNormalizer`, because Entra identifies principals with `tid`, `oid`, and `idtyp` whereas generic OIDC uses `iss` and `sub`; and retain `IGroupMembershipResolver` as an optional capability because Entra group overage needs an out-of-band lookup.

The missing pieces are value contracts, not interfaces: `ExternalPrincipalKey`, `NormalizedPrincipal`, and a three-state `GroupResolutionResult`. Provider registration contributes unique scheme-routing metadata. Extensibility is clean packages composed explicitly through dependency injection. Adding a provider may require a one-line composition change, but not a business-logic change. Arbitrary assembly scanning and runtime loading of untrusted assemblies are rejected.

### 2. Route bearer tokens through one policy scheme

Every bearer handler has a unique named scheme. One policy scheme is the default authenticate and challenge scheme and forwards through a selector. The selector may inspect an unsigned issuer and, if needed, audience only to route; the selected handler validates the token. Overlapping rules fail at startup, and malformed or unroutable tokens select a deterministic fallback scheme.

This avoids policies that list every bearer scheme. Such a policy authenticates each handler in turn and, when all fail, challenges each one, yielding one 401 with multiple appended `WWW-Authenticate` headers and potentially conflicting challenge events. The router challenges only its selected fallback.

### 3. Configure and validate Entra ID through Microsoft.Identity.Web

Use the Microsoft.Identity.Web authentication-builder extension that protects a web API, with an explicit bearer scheme name because multiple providers coexist. The conventional `AzureAd` configuration section contains `Instance`, `TenantId`, `ClientId`, and optional `Audience` for a non-default Application ID URI.

A tenant GUID produces single-tenant validation. Microsoft.Identity.Web installs its Entra issuer validator and signing-key issuer, audience, signature, and lifetime validation; no additional issuer validator is needed. Optional multi-tenant operation uses `organizations`, retains that validation, and extends the existing token-validated event to require a validated GUID `tid` in the configured allowlist. Never replace issuer validation with a bare tenant-claim check.

To reject personal Microsoft accounts, configure the application registration for multiple organizations and use `organizations`; `common` permits personal accounts. Application roles arrive as the `roles` string array and group object IDs as the `groups` GUID array. Disable default inbound claim mapping so these raw names remain deterministic rather than becoming long URI claim types.

### 4. Resolve group overage fail closed

Detect overage from a `claim-names` claim containing a `groups` member and also recognize `has-groups`. Never follow an endpoint supplied in `claim-sources`. When Microsoft Graph is used, construct the request from validated tenant and object identifiers instead.

The result is three-way. A known grant proceeds. Complete membership with no matching grant, or an explicit deny, returns 403 Problem Details with error code `authorization_denied`. Membership that could not be resolved and might have granted access returns 503 with `authorization_indeterminate`. An absent `groups` claim is first checked for an overage indicator; only a provider contract known always to emit that indicator may treat a claimless response as empty.

The optional Graph-backed resolver is disabled by default, uses least-privilege permissions and secret references or managed identity, retains credentials only on the server, and has a short configurable cache TTL. It returns complete membership or indeterminate. Timeouts, throttling, authorization failure, and Graph outage never become an empty set or a stale grant.

### 5. Use a composite immutable identity key

Identify a principal by the tuple of stable provider-instance identifier, validated issuer, tenant identifier or null, principal kind, and immutable subject identifier. Entra derives it from `tid`, `oid`, and `idtyp`; `idtyp` requires the corresponding optional claim to be configured, `app` denotes a service principal, and `user` a delegated user. Generic OIDC uses its validated issuer and subject with an explicitly configured principal kind. Control-database local role assignments are keyed by this composite key and the role identifier.

Never use `name`, `preferred_username`, `upn`, `email`, or `unique_name` as authorization keys: they are mutable and reassignable. An object identifier without tenant and provider context is ambiguous, as is a subject without issuer. `roles`, `groups`, `scope`, session, and token identifiers are entitlement or session claims, not identity.

### 6. Apply deterministic union-based role mapping

Evaluate terminal deny or disabled-principal state first; it is absolute. Otherwise, effective permissions are the union of all positive grants: control-database assignments, mapped Entra application roles, mapped directory group object IDs, and mapped generic OIDC role and group claims. Positive sources do not override one another. Unrecognized values grant nothing and are audited.

When no grant exists, deny only if every relevant source is complete; otherwise return the indeterminate outcome from Decision 4. This ordering means removing a permission cannot grant more access: the result is a pure union of positive grants gated by absolute deny, so removing a grant only shrinks it. Lifting a terminal disabled state can increase access, but that is an audited administrative state change, not permission removal. Group mapping uses immutable object IDs, never display names. Administrator bootstrap uses a configured application role or immutable group object ID, never an email address or domain.

### 7. Exclude the Development provider from production artifacts

A runtime environment check alone is insufficient: an attacker controlling environment variables can claim Development. Exclude the development authentication assembly from the Production publish artifact. Its registration also checks the host environment and throws unless it is exactly Development or Test, while startup options validation independently rejects enabled configuration. CI asserts that the Production artifact lacks the assembly and that no plugin probing path can load arbitrary files.

### 8. Protect endpoints by default and test the exception

Configure an authenticated fallback authorization policy so endpoints are protected by default. An automated post-startup test enumerates the endpoint data source. For every routed endpoint with HTTP method metadata, it requires either authorization metadata or an allow-anonymous marker accompanied by non-empty custom anonymous-access-justification metadata, never neither and never both. Failure reports the route pattern and endpoint display name. Thus, no protected endpoint is accidentally anonymous is mechanically enforced rather than a review convention.

### 9. Stream authenticated SSE with fetch

Native browser `EventSource` cannot attach an `Authorization` header, and query-string tokens are forbidden. The client uses `fetch` with the header and `Accept: text/event-stream`, and manually sends the last-event identifier on reconnect. On every stream open, the server authenticates and authorizes the specific job resource, not merely the initial connection, and closes at token expiry to force revalidation. A 401 causes one token reacquisition and reconnect; a second consecutive 401 is terminal authentication failure. A 403 is immediately terminal.

The server persists ordered immutable event identifiers, payload, type, and a retention boundary per job. Resumption sends only events strictly after the supplied identifier. An expired cursor requires full snapshot reload, not a guess. The client tracks the highest applied identifier and drops repeats.

### 10. Test registered bearer schemes with an in-process issuer

Tests exercise the real registered bearer schemes against an in-process OIDC discovery and JWKS issuer. Replacing authentication with a fake handler that injects a principal is prohibited because it tests none of the validation. The issuer publishes exact issuer metadata, a JWKS endpoint, and a public key whose identifier and algorithm match while retaining the private signing key.

Test correct and wrong issuer; correct and wrong audience; allowlisted tenant and tenant/issuer mismatch; valid signature, wrong signature, and unknown key; expired and not-yet-valid tokens; application roles; direct groups; group overage; absent groups; correct, missing, and wrong scope; and consistent user versus service-principal tokens.

## Deleted Abstractions

| Proposed interface | Verdict | Replacement |
| --- | --- | --- |
| `IAuthProviderRegistration` | Keep | Per-provider scheme setup, configuration, and routing vary. |
| `IAuthProviderConfigurationValidator` | Delete | Framework options validation with validate-on-start. |
| `IExternalPrincipalNormalizer` | Keep | Entra and generic OIDC use different immutable identity claim shapes. |
| `IGroupMembershipResolver` | Keep, optional | Out-of-band resolution is needed only for Entra group overage. |
| `IRoleMappingService` | Delete | One provider-neutral control-database mapping service. |
| `IPermissionEvaluator` | Delete | Framework authorization handler. |
| `ICurrentActorAccessor` | Delete | Pass normalized current actor from the HTTP boundary. |
| `IAuthorizationAuditEnricher` | Delete | The normalized actor already contains audit identity. |

## Consequences

Business logic receives a normalized current actor and permissions, not provider claims or authentication plumbing. Provider additions are explicit composition changes; routing collisions fail before serving traffic. The system denies incomplete group information and unrecognized entitlement values unless complete sources establish no grant.

SSE requires a custom fetch-based client, durable job event retention, and snapshot recovery.

## Alternatives Considered

The eight-interface proposal was rejected because five interfaces had no provider variability. Listing all bearer schemes was rejected because it produces multi-challenge 401 responses. An environment-only development-provider guard was rejected because its assembly remains available to a manipulated process. Following `claim-sources` endpoints was rejected because token data must not direct privileged outbound requests.

Fake-principal tests were rejected because they bypass discovery, key selection, signature, issuer, audience, and lifetime validation. Real-tenant tests alone were rejected for pull requests because they require credentials and are not deterministic. Only a real tenant can test Entra token issuance, application-registration and consent errors, Conditional Access and continuous access evaluation, live signing-key rollover, guest and personal-account edge cases, and Graph permissions, throttling, and overage generation. A separately, manually or protectively triggered smoke suite may cover these cases; pull-request tests remain credential-free.

## Verification

Verify startup rejects overlapping router metadata, chooses the fallback for malformed and unroutable tokens, and yields one fallback challenge. Verify Entra single-tenant, multi-tenant allowlist, raw claim-name, and personal-account restrictions without replacing issuer validation. Verify all group outcomes, including absent groups with and without a trustworthy overage indicator, and that Graph failures return indeterminate rather than empty membership or stale grants.

Test immutable key construction for user and service-principal Entra tokens and generic OIDC principals, union precedence, terminal deny, and administrator bootstrap restrictions. Inspect the Production publish artifact in CI for the absent development assembly and absence of plugin probing. Run the endpoint-enumeration safety-net test. Exercise SSE reconnect, expiry, repeated 401, 403, duplicate event, retention-boundary, and authorization-on-every-open behavior. Run the deterministic token matrix in Decision 10 against the real registered schemes.

## Open Questions

- What stable provider-instance identifier format and migration policy will distinguish separately registered instances of the same generic OIDC issuer?
- If Graph resolution is enabled, what cache TTL, request budget, and operational owner are appropriate for DataPitcher's availability requirements?
- Which protected environment and trigger will run the real-tenant smoke suite, and how will its credentials and results be isolated from ordinary pull requests?
