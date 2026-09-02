# DataPitcher Slice 4: Provider-Neutral Authorization Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a dependency-free permission and authorization model that makes immutable principal identity, role unions, terminal denial, and indeterminate group membership explicit and fully unit-testable.

**Architecture:** `DataPitcher.Core` owns the closed permission vocabulary, immutable permission sets, and named role bundles while retaining its zero-dependency boundary. `DataPitcher.Auth.Abstractions`, which references Core only, owns normalized identity and pure authorization inputs, decisions, and evaluation; it accepts already-normalized positive grants rather than claims, tokens, or HTTP context. Later provider and API slices will normalize validated claims, resolve memberships, persist mappings and audit events, and translate the typed outcome at the HTTP boundary without changing these rules.

**Tech Stack:** .NET SDK 10.0.400, C# latest, xUnit 2.9.3, FsCheck.Xunit 3.3.2, Coverlet collector, XML project inspection, Bash.

---

## File Structure

- `DataPitcher.sln` — solution entry for the new Auth.Abstractions assembly.
- `src/DataPitcher.Core/Authorization/Permission.cs` — closed, dotted permission vocabulary.
- `src/DataPitcher.Core/Authorization/PermissionSet.cs` — immutable permission collection and set operations.
- `src/DataPitcher.Core/Authorization/Role.cs` — four named role identifiers.
- `src/DataPitcher.Core/Authorization/RoleBundles.cs` — exact Viewer, Planner, Operator, and Administrator bundles.
- `src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj` — provider-neutral identity and authorization assembly referencing Core only.
- `src/DataPitcher.Auth.Abstractions/Identity/ExternalPrincipalKey.cs` — validated composite immutable authorization key.
- `src/DataPitcher.Auth.Abstractions/Identity/NormalizedPrincipal.cs` — immutable key plus non-authoritative presentation claims.
- `src/DataPitcher.Auth.Abstractions/Identity/GroupResolutionResult.cs` — not-applicable, complete, or indeterminate group-resolution value.
- `src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationModels.cs` — role-grant sources, terminal state, typed request, decision, and auditable state transition.
- `src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationEvaluator.cs` — terminal-first, union-based, three-outcome evaluator.
- `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs` — Auth.Abstractions-to-Core-only project-boundary assertion.
- `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj` — Auth.Abstractions and FsCheck.Xunit test references.
- `tests/DataPitcher.UnitTests/Authorization/ExternalPrincipalKeyTests.cs` — composite-key validation and mutable-claim separation tests.
- `tests/DataPitcher.UnitTests/Authorization/GroupResolutionResultTests.cs` — all three group-result states and defensive-copy tests.
- `tests/DataPitcher.UnitTests/Authorization/PermissionAndRoleBundleTests.cs` — vocabulary, bundle, and permission-set tests.
- `tests/DataPitcher.UnitTests/Authorization/AuthorizationEvaluatorTests.cs` — precedence, outcome, presentation-key, and audit-transition tests.
- `tests/DataPitcher.UnitTests/Authorization/AuthorizationPropertyTests.cs` — FsCheck determinism and monotonicity properties.

## Scope and Deferrals

This slice is deliberately the provider-neutral half of ADR 0006. Permission evaluation is pure domain logic, so it must be fully testable with no web host, token, network, database, Docker container, ASP.NET package, data-access package, or provider package. Core continues to reference nothing; the existing architecture test is extended rather than weakened. Auth.Abstractions may reference Core, but neither project may reference API, Entra, OIDC, SQL Server, PostgreSQL, or any authentication framework.

The new Auth.Abstractions project contains only the value contracts needed by this slice. It does not yet define provider implementations or host registration. `IAuthProviderRegistration`, `IExternalPrincipalNormalizer`, and `IGroupMembershipResolver` remain provider/HTTP-slice work because their useful signatures must be driven by concrete scheme and validated-claim mechanics; no fake generic registration API is useful now. In particular, do not add ADR 0006's rejected `IAuthProviderConfigurationValidator`, `IRoleMappingService`, `IPermissionEvaluator`, `ICurrentActorAccessor`, or `IAuthorizationAuditEnricher`. Positive `RoleGrant` values and the candidate roles from an indeterminate group result are inputs from the later provider-neutral control-database mapping service, not a new interface in this slice.

The immutable authorization key is exactly provider instance, validated issuer, nullable tenant, principal kind, and immutable subject. `NormalizedPrincipal` separately carries display name, email, username, and principal name for presentation and audit context only. No caller may use any of those mutable values to select assignments; direct group mappings likewise supply only immutable object identifiers in later slices, never display names.

Role bundles are deliberately fixed here: Viewer receives every read permission; Planner receives Viewer plus connection, schema, selection, and plan write permissions, `Selections.RawSql`, and `Plans.Seal`; Operator receives Viewer plus transfer start and the three explicit transfer overrides; Administrator receives the complete vocabulary, including `AuthProviders.Manage`, `RoleMappings.Manage`, and `Audit.Write`. A principal may receive multiple positive roles, and their permissions union. This preserves the least surprising composition: adding a source cannot remove access and removing a grant cannot add it.

`GroupResolutionResult.NotApplicable`, `.Complete`, and `.Indeterminate` make absence, a known empty membership set, and unresolved membership different values. An unresolved group source produces `Indeterminate` only when one of its possible mapped roles could supply the requested permission; a known direct permission still grants. A terminal deny or disabled principal is absolute and returns `Denied` before grants are examined. Re-enabling a disabled principal is represented by an auditable `PrincipalStateChange`, with persistence deferred to Infrastructure; it is not modeled as deleting a grant.

All handwritten production code added here must reach 100 percent line, branch, and method coverage. Coverage is merged and enforced only by `scripts/test-all.sh`; do not add a second gate or weaken that script. This slice's runnable lane is exclusively `scripts/test-unit.sh`, whose coverage output is informational, and it needs no Docker. The merged Docker-containing suite remains the repository-wide enforcement lane when it is available.

### Task 1: Add the Core-only Auth.Abstractions project boundary

**Files:**
- Create: `src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj`
- Modify: `DataPitcher.sln`
- Test: `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`

- [ ] **Step 1: Write the failing project-boundary test.** Add this complete test method to `DependencyRuleTests`:

```csharp
[Fact]
public void AuthAbstractions_ReferencesCoreAndHasNoPackages()
{
    var project = Project("DataPitcher.Auth.Abstractions");
    Assert.Equal(["DataPitcher.Core"], References(project));
    Assert.Empty(Packages(project));
}
```

- [ ] **Step 2: Run the architecture test and confirm it fails because the project is absent.** Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~AuthAbstractions_ReferencesCoreAndHasNoPackages"`. Expected: FAIL with `InvalidOperationException: Sequence contains no matching element` from `Project("DataPitcher.Auth.Abstractions")`.

- [ ] **Step 3: Create the minimal Core-only project file.** Write `src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj` exactly as follows:

```xml
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.Abstractions</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Core/DataPitcher.Core.csproj" /></ItemGroup></Project>
```

- [ ] **Step 4: Add the new project to the solution.** Run: `dotnet sln DataPitcher.sln add src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj`.

- [ ] **Step 5: Run the architecture test and confirm the Core-only boundary passes.** Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~AuthAbstractions_ReferencesCoreAndHasNoPackages"`. Expected: `Passed: 1. Failed: 0.`

- [ ] **Step 6: Commit the isolated project-boundary change.** Run: `git add DataPitcher.sln src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs && git commit -m "feat: add auth abstractions boundary"`.

### Task 2: Model immutable principals and three-state group resolution

**Files:**
- Create: `src/DataPitcher.Auth.Abstractions/Identity/ExternalPrincipalKey.cs`, `src/DataPitcher.Auth.Abstractions/Identity/NormalizedPrincipal.cs`, `src/DataPitcher.Auth.Abstractions/Identity/GroupResolutionResult.cs`
- Modify: `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`
- Test: `tests/DataPitcher.UnitTests/Authorization/ExternalPrincipalKeyTests.cs`, `tests/DataPitcher.UnitTests/Authorization/GroupResolutionResultTests.cs`

- [ ] **Step 1: Add the Auth.Abstractions test reference and write the failing value-contract tests.** Add `<ProjectReference Include="../../src/DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj" />` beside the existing Core reference, then create the following complete tests:

```csharp
using DataPitcher.Auth.Abstractions.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class ExternalPrincipalKeyTests
{
    [Fact]
    public void ExternalPrincipalKey_WhenAnyImmutableComponentDiffers_IsNotEqual()
    {
        var key = new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.User, "object-a");
        Assert.Equal(key, new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.User, "object-a"));
        Assert.NotEqual(key, new ExternalPrincipalKey("oidc-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.User, "object-a"));
        Assert.NotEqual(key, new ExternalPrincipalKey("entra-prod", "https://login.example/other", "tenant-a", PrincipalKind.User, "object-a"));
        Assert.NotEqual(key, new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", null, PrincipalKind.User, "object-a"));
        Assert.NotEqual(key, new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.ServicePrincipal, "object-a"));
        Assert.NotEqual(key, new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.User, "object-b"));
    }

    [Theory]
    [InlineData("", "https://login.example/tenant", "subject")]
    [InlineData("provider", "not-an-issuer", "subject")]
    [InlineData("provider", "https://login.example/tenant", "")]
    public void ExternalPrincipalKey_WhenRequiredIdentityPartIsInvalid_Throws(string provider, string issuer, string subject) =>
        Assert.Throws<ArgumentException>(() => new ExternalPrincipalKey(provider, issuer, "tenant-a", PrincipalKind.User, subject));

    [Fact]
    public void ExternalPrincipalKey_WhenTenantIsEmptyRatherThanNull_Throws() =>
        Assert.Throws<ArgumentException>(() => new ExternalPrincipalKey("provider", "https://login.example/tenant", "", PrincipalKind.User, "subject"));

    [Fact]
    public void NormalizedPrincipal_WhenPresentationClaimsChange_KeepsTheSameAuthorizationKey()
    {
        var key = new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.User, "object-a");
        var first = new NormalizedPrincipal(key, new PrincipalPresentation("Ada", "ada@example.test", "ada", "ada@example.test"));
        var renamed = new NormalizedPrincipal(key, new PrincipalPresentation("Grace", "grace@example.test", "grace", "grace@example.test"));
        Assert.Equal(first.AuthorizationKey, renamed.AuthorizationKey);
        Assert.NotEqual(first.Presentation, renamed.Presentation);
    }
}
```

```csharp
using DataPitcher.Auth.Abstractions.Identity;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class GroupResolutionResultTests
{
    [Fact]
    public void GroupResolutionResult_NotApplicable_HasItsOwnStateAndNoGroups()
    {
        var result = GroupResolutionResult.NotApplicable();
        Assert.Equal(GroupResolutionState.NotApplicable, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Complete_PreservesKnownEmptyMembership()
    {
        var result = GroupResolutionResult.Complete([]);
        Assert.Equal(GroupResolutionState.Complete, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Complete_DefensivelyCopiesImmutableGroupIdentifiers()
    {
        var identifiers = new List<string> { "group-b", "group-a", "group-a" };
        var result = GroupResolutionResult.Complete(identifiers);
        identifiers.Clear();
        Assert.Equal(["group-a", "group-b"], result.ImmutableGroupIds);
    }

    [Fact]
    public void GroupResolutionResult_Indeterminate_DiscardsAnyMembershipAndIsNotComplete()
    {
        var result = GroupResolutionResult.Indeterminate();
        Assert.Equal(GroupResolutionState.Indeterminate, result.State);
        Assert.Empty(result.ImmutableGroupIds);
    }
}
```

- [ ] **Step 2: Run the value-contract tests and confirm the missing namespace is the failure.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ExternalPrincipalKeyTests|FullyQualifiedName~GroupResolutionResultTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'Identity' does not exist in the namespace 'DataPitcher.Auth.Abstractions'`.

- [ ] **Step 3: Implement the three value contracts without claims, tokens, or mutable authorization keys.** Write the following complete production code:

```csharp
// src/DataPitcher.Auth.Abstractions/Identity/ExternalPrincipalKey.cs
namespace DataPitcher.Auth.Abstractions.Identity;

public enum PrincipalKind { User, ServicePrincipal }

public sealed record ExternalPrincipalKey
{
    public ExternalPrincipalKey(string providerInstance, string validatedIssuer, string? tenantId, PrincipalKind principalKind, string immutableSubject)
    {
        if (string.IsNullOrWhiteSpace(providerInstance) || string.IsNullOrWhiteSpace(immutableSubject)) throw new ArgumentException("Provider instance and immutable subject are required.");
        if (!Uri.TryCreate(validatedIssuer, UriKind.Absolute, out _)) throw new ArgumentException("Validated issuer must be an absolute URI.", nameof(validatedIssuer));
        if (tenantId is not null && string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant must be null or non-empty.", nameof(tenantId));
        ProviderInstance = providerInstance; ValidatedIssuer = validatedIssuer; TenantId = tenantId; PrincipalKind = principalKind; ImmutableSubject = immutableSubject;
    }
    public string ProviderInstance { get; }
    public string ValidatedIssuer { get; }
    public string? TenantId { get; }
    public PrincipalKind PrincipalKind { get; }
    public string ImmutableSubject { get; }
}
```

```csharp
// src/DataPitcher.Auth.Abstractions/Identity/NormalizedPrincipal.cs
namespace DataPitcher.Auth.Abstractions.Identity;

public sealed record PrincipalPresentation(string? DisplayName, string? Email, string? Username, string? PrincipalName);
public sealed record NormalizedPrincipal(ExternalPrincipalKey AuthorizationKey, PrincipalPresentation Presentation);
```

```csharp
// src/DataPitcher.Auth.Abstractions/Identity/GroupResolutionResult.cs
namespace DataPitcher.Auth.Abstractions.Identity;

public enum GroupResolutionState { NotApplicable, Complete, Indeterminate }

public sealed class GroupResolutionResult
{
    private GroupResolutionResult(GroupResolutionState state, IEnumerable<string> immutableGroupIds)
    {
        State = state;
        ImmutableGroupIds = Array.AsReadOnly(immutableGroupIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
    public GroupResolutionState State { get; }
    public IReadOnlyCollection<string> ImmutableGroupIds { get; }
    public static GroupResolutionResult NotApplicable() => new(GroupResolutionState.NotApplicable, []);
    public static GroupResolutionResult Complete(IEnumerable<string> immutableGroupIds) => new(GroupResolutionState.Complete, immutableGroupIds);
    public static GroupResolutionResult Indeterminate() => new(GroupResolutionState.Indeterminate, []);
}
```

- [ ] **Step 4: Run the value-contract tests and confirm they pass.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~ExternalPrincipalKeyTests|FullyQualifiedName~GroupResolutionResultTests"`. Expected: `Passed: 10. Failed: 0.`

- [ ] **Step 5: Commit the immutable identity and group-result contracts.** Run: `git add src/DataPitcher.Auth.Abstractions/Identity tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj tests/DataPitcher.UnitTests/Authorization/ExternalPrincipalKeyTests.cs tests/DataPitcher.UnitTests/Authorization/GroupResolutionResultTests.cs && git commit -m "feat: add normalized authorization identities"`.

### Task 3: Define the permission vocabulary and named role bundles in Core

**Files:**
- Create: `src/DataPitcher.Core/Authorization/Permission.cs`, `src/DataPitcher.Core/Authorization/PermissionSet.cs`, `src/DataPitcher.Core/Authorization/Role.cs`, `src/DataPitcher.Core/Authorization/RoleBundles.cs`
- Modify: none
- Test: `tests/DataPitcher.UnitTests/Authorization/PermissionAndRoleBundleTests.cs`

- [ ] **Step 1: Write the failing Core vocabulary and bundle tests.** Create this complete test file:

```csharp
using DataPitcher.Core.Authorization;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class PermissionAndRoleBundleTests
{
    [Fact]
    public void Permissions_ExposeTheCompleteClosedVocabulary() =>
        Assert.Equal(["Audit.Read", "Audit.Write", "AuthProviders.Manage", "Connections.Read", "Connections.Write", "Plans.Read", "Plans.Seal", "Plans.Write", "RoleMappings.Manage", "Schema.Read", "Schema.Write", "Selections.RawSql", "Selections.Read", "Selections.Write", "Transfers.ConstraintOverride", "Transfers.Read", "Transfers.Start", "Transfers.TriggerOverride", "Transfers.UsePotentiallyLossyMapping", "Transfers.Write"], Permissions.All.Select(permission => permission.Value));

    [Fact]
    public void RoleBundles_ViewerHasEveryReadPermissionOnly() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersRead]), RoleBundles.For(Role.Viewer));

    [Fact]
    public void RoleBundles_PlannerAddsPlanningWritesAndNoTransferStart() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.ConnectionsWrite, Permissions.PlansRead, Permissions.PlansSeal, Permissions.PlansWrite, Permissions.SchemaRead, Permissions.SchemaWrite, Permissions.SelectionsRawSql, Permissions.SelectionsRead, Permissions.SelectionsWrite, Permissions.TransfersRead]), RoleBundles.For(Role.Planner));

    [Fact]
    public void RoleBundles_OperatorAddsTransferExecutionAndOverrides() =>
        Assert.Equal(new PermissionSet([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersConstraintOverride, Permissions.TransfersRead, Permissions.TransfersStart, Permissions.TransfersTriggerOverride, Permissions.TransfersUsePotentiallyLossyMapping]), RoleBundles.For(Role.Operator));

    [Fact]
    public void RoleBundles_AdministratorHasEveryPermission() => Assert.Equal(new PermissionSet(Permissions.All), RoleBundles.For(Role.Administrator));

    [Fact]
    public void PermissionSet_UnionAndRemovalRemainImmutableAndMonotonic()
    {
        var full = new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart]);
        var reduced = full.Without(Permissions.PlansSeal);
        Assert.True(reduced.IsSubsetOf(full));
        Assert.True(full.Contains(Permissions.PlansSeal));
        Assert.Equal(full, reduced.Union(new PermissionSet([Permissions.PlansSeal])));
        Assert.Equal(full.GetHashCode(), new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart]).GetHashCode());
        Assert.True(full.Equals((object)new PermissionSet([Permissions.PlansSeal, Permissions.TransfersStart])));
        Assert.False(full.Equals(null));
    }

    [Fact]
    public void RoleBundles_WhenRoleIsUnknown_RejectsIt() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleBundles.For((Role)99));
}
```

- [ ] **Step 2: Run the Core tests and confirm they fail for the missing authorization namespace.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~PermissionAndRoleBundleTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'Authorization' does not exist in the namespace 'DataPitcher.Core'`.

- [ ] **Step 3: Implement the closed vocabulary, immutable set, and exact bundles in Core.** Write the following complete production code:

```csharp
// src/DataPitcher.Core/Authorization/Permission.cs
namespace DataPitcher.Core.Authorization;

public sealed record Permission
{
    private Permission(string value) => Value = value;
    public string Value { get; }
}

public static class Permissions
{
    public static Permission ConnectionsRead { get; } = new("Connections.Read"); public static Permission ConnectionsWrite { get; } = new("Connections.Write");
    public static Permission SchemaRead { get; } = new("Schema.Read"); public static Permission SchemaWrite { get; } = new("Schema.Write");
    public static Permission SelectionsRead { get; } = new("Selections.Read"); public static Permission SelectionsWrite { get; } = new("Selections.Write"); public static Permission SelectionsRawSql { get; } = new("Selections.RawSql");
    public static Permission PlansRead { get; } = new("Plans.Read"); public static Permission PlansWrite { get; } = new("Plans.Write"); public static Permission PlansSeal { get; } = new("Plans.Seal");
    public static Permission TransfersRead { get; } = new("Transfers.Read"); public static Permission TransfersWrite { get; } = new("Transfers.Write"); public static Permission TransfersStart { get; } = new("Transfers.Start"); public static Permission TransfersConstraintOverride { get; } = new("Transfers.ConstraintOverride"); public static Permission TransfersTriggerOverride { get; } = new("Transfers.TriggerOverride"); public static Permission TransfersUsePotentiallyLossyMapping { get; } = new("Transfers.UsePotentiallyLossyMapping");
    public static Permission AuditRead { get; } = new("Audit.Read"); public static Permission AuditWrite { get; } = new("Audit.Write"); public static Permission AuthProvidersManage { get; } = new("AuthProviders.Manage"); public static Permission RoleMappingsManage { get; } = new("RoleMappings.Manage");
    public static IReadOnlyCollection<Permission> All { get; } = Array.AsReadOnly([AuditRead, AuditWrite, AuthProvidersManage, ConnectionsRead, ConnectionsWrite, PlansRead, PlansSeal, PlansWrite, RoleMappingsManage, SchemaRead, SchemaWrite, SelectionsRawSql, SelectionsRead, SelectionsWrite, TransfersConstraintOverride, TransfersRead, TransfersStart, TransfersTriggerOverride, TransfersUsePotentiallyLossyMapping, TransfersWrite]);
}
```

```csharp
// src/DataPitcher.Core/Authorization/PermissionSet.cs
namespace DataPitcher.Core.Authorization;

public sealed class PermissionSet : IEquatable<PermissionSet>
{
    private readonly HashSet<Permission> permissions;
    public PermissionSet(IEnumerable<Permission> permissions)
    {
        this.permissions = [.. permissions];
        Permissions = Array.AsReadOnly(this.permissions.OrderBy(permission => permission.Value, StringComparer.Ordinal).ToArray());
    }
    public static PermissionSet Empty { get; } = new([]);
    public IReadOnlyCollection<Permission> Permissions { get; }
    public bool Contains(Permission permission) => permissions.Contains(permission);
    public PermissionSet Union(PermissionSet other) => new(permissions.Concat(other.permissions));
    public PermissionSet Without(Permission permission) => new(permissions.Where(candidate => candidate != permission));
    public bool IsSubsetOf(PermissionSet other) => permissions.IsSubsetOf(other.permissions);
    public bool Equals(PermissionSet? other) => other is not null && permissions.SetEquals(other.permissions);
    public override bool Equals(object? obj) => obj is PermissionSet other && Equals(other);
    public override int GetHashCode() => permissions.Aggregate(0, (hash, permission) => hash ^ permission.GetHashCode());
}
```

```csharp
// src/DataPitcher.Core/Authorization/Role.cs
namespace DataPitcher.Core.Authorization;

public enum Role { Viewer, Planner, Operator, Administrator }
```

```csharp
// src/DataPitcher.Core/Authorization/RoleBundles.cs
namespace DataPitcher.Core.Authorization;

public static class RoleBundles
{
    private static readonly PermissionSet Viewer = new([Permissions.AuditRead, Permissions.ConnectionsRead, Permissions.PlansRead, Permissions.SchemaRead, Permissions.SelectionsRead, Permissions.TransfersRead]);
    private static readonly PermissionSet Planner = Viewer.Union(new([Permissions.ConnectionsWrite, Permissions.PlansSeal, Permissions.PlansWrite, Permissions.SchemaWrite, Permissions.SelectionsRawSql, Permissions.SelectionsWrite]));
    private static readonly PermissionSet Operator = Viewer.Union(new([Permissions.TransfersConstraintOverride, Permissions.TransfersStart, Permissions.TransfersTriggerOverride, Permissions.TransfersUsePotentiallyLossyMapping]));
    private static readonly PermissionSet Administrator = new(Permissions.All);
    public static PermissionSet For(Role role) => role switch { Role.Viewer => Viewer, Role.Planner => Planner, Role.Operator => Operator, Role.Administrator => Administrator, _ => throw new ArgumentOutOfRangeException(nameof(role)) };
}
```

- [ ] **Step 4: Run the vocabulary and bundle tests and confirm they pass.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~PermissionAndRoleBundleTests"`. Expected: `Passed: 7. Failed: 0.`

- [ ] **Step 5: Commit the Core-only authorization vocabulary.** Run: `git add src/DataPitcher.Core/Authorization tests/DataPitcher.UnitTests/Authorization/PermissionAndRoleBundleTests.cs && git commit -m "feat: add authorization permission bundles"`.

### Task 4: Evaluate terminal state, role unions, and indeterminate access deterministically

**Files:**
- Create: `src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationModels.cs`, `src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationEvaluator.cs`
- Modify: `tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj`
- Test: `tests/DataPitcher.UnitTests/Authorization/AuthorizationEvaluatorTests.cs`, `tests/DataPitcher.UnitTests/Authorization/AuthorizationPropertyTests.cs`

- [ ] **Step 1: Add FsCheck.Xunit and write the failing precedence, outcome, audit, and property tests.** Add `<PackageReference Include="FsCheck.Xunit" Version="3.3.2" />` to `DataPitcher.UnitTests.csproj`, then create these complete test files:

```csharp
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class AuthorizationEvaluatorTests
{
    [Theory]
    [InlineData(PrincipalAuthorizationState.TerminalDeny)]
    [InlineData(PrincipalAuthorizationState.Disabled)]
    public void AuthorizationEvaluator_WhenPrincipalIsTerminal_ReturnsDeniedBeforeGrantUnion(PrincipalAuthorizationState state)
    {
        var decision = AuthorizationEvaluator.Evaluate(Input(state, [new(Role.Administrator, RoleGrantSource.ControlDatabaseAssignment)]), Permissions.AuthProvidersManage);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal(PermissionSet.Empty, decision.EffectivePermissions);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenPositiveSourcesGrantDifferentRoles_UsesTheirUnion()
    {
        var grants = new[] { new RoleGrant(Role.Planner, RoleGrantSource.ApplicationRole), new RoleGrant(Role.Operator, RoleGrantSource.DirectoryGroup), new RoleGrant(Role.Administrator, RoleGrantSource.OpenIdConnectRole), new RoleGrant(Role.Viewer, RoleGrantSource.OpenIdConnectGroup) };
        Assert.Equal(AuthorizationOutcome.Granted, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, grants), Permissions.TransfersStart).Outcome);
        Assert.Equal(AuthorizationOutcome.Granted, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, grants), Permissions.AuthProvidersManage).Outcome);
    }

    [Theory]
    [InlineData(GroupResolutionState.NotApplicable)]
    [InlineData(GroupResolutionState.Complete)]
    public void AuthorizationEvaluator_WhenAllRelevantSourcesAreComplete_ReturnsDenied(GroupResolutionState state)
    {
        var groups = state == GroupResolutionState.Complete ? GroupResolutionResult.Complete([]) : GroupResolutionResult.NotApplicable();
        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], groups, [Role.Operator]), Permissions.TransfersStart).Outcome);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenIndeterminateGroupsCouldGrantRequestedPermission_ReturnsIndeterminate() =>
        Assert.Equal(AuthorizationOutcome.Indeterminate, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], GroupResolutionResult.Indeterminate(), [Role.Operator]), Permissions.TransfersStart).Outcome);

    [Fact]
    public void AuthorizationEvaluator_WhenIndeterminateGroupsCannotGrantRequestedPermission_ReturnsDenied() =>
        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [], GroupResolutionResult.Indeterminate(), [Role.Planner]), Permissions.TransfersStart).Outcome);

    [Fact]
    public void AuthorizationEvaluator_WhenKnownGrantExists_DoesNotTurnItIntoIndeterminate()
    {
        var decision = AuthorizationEvaluator.Evaluate(Input(PrincipalAuthorizationState.Active, [new(Role.Operator, RoleGrantSource.ControlDatabaseAssignment)], GroupResolutionResult.Indeterminate(), [Role.Planner]), Permissions.TransfersStart);
        Assert.Equal(AuthorizationOutcome.Granted, decision.Outcome);
    }

    [Fact]
    public void AuthorizationEvaluator_WhenPresentationClaimsChange_UsesOnlyTheImmutableAuthorizationKey()
    {
        var key = new ExternalPrincipalKey("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject");
        var grants = new[] { new RoleGrant(Role.Operator, RoleGrantSource.ControlDatabaseAssignment) };
        var first = Input(PrincipalAuthorizationState.Active, grants, principal: new(key, new("Ada", "ada@example.test", "ada", "ada@example.test")));
        var renamed = Input(PrincipalAuthorizationState.Active, grants, principal: new(key, new("Grace", "grace@example.test", "grace", "grace@example.test")));
        Assert.Equal(AuthorizationEvaluator.Evaluate(first, Permissions.TransfersStart), AuthorizationEvaluator.Evaluate(renamed, Permissions.TransfersStart));
    }

    [Fact]
    public void PrincipalStateChange_WhenDisabledStateIsLifted_IsAuditableAndNotAGrantRemoval()
    {
        var disabled = Input(PrincipalAuthorizationState.Disabled, [new(Role.Operator, RoleGrantSource.ControlDatabaseAssignment)]);
        var enabled = Input(PrincipalAuthorizationState.Active, disabled.PositiveGrants);
        var change = new PrincipalStateChange(disabled.Principal.AuthorizationKey, PrincipalAuthorizationState.Disabled, PrincipalAuthorizationState.Active, disabled.Principal.AuthorizationKey, DateTimeOffset.UnixEpoch);
        Assert.Equal(AuthorizationOutcome.Denied, AuthorizationEvaluator.Evaluate(disabled, Permissions.TransfersStart).Outcome);
        Assert.True(change.RequiresAudit);
        Assert.True(new PrincipalStateChange(disabled.Principal.AuthorizationKey, PrincipalAuthorizationState.TerminalDeny, PrincipalAuthorizationState.Active, disabled.Principal.AuthorizationKey, DateTimeOffset.UnixEpoch).RequiresAudit);
        Assert.False(new PrincipalStateChange(disabled.Principal.AuthorizationKey, PrincipalAuthorizationState.Active, PrincipalAuthorizationState.Disabled, disabled.Principal.AuthorizationKey, DateTimeOffset.UnixEpoch).RequiresAudit);
        Assert.Equal(disabled.PositiveGrants, enabled.PositiveGrants);
        Assert.Equal(AuthorizationOutcome.Granted, AuthorizationEvaluator.Evaluate(enabled, Permissions.TransfersStart).Outcome);
    }

    private static AuthorizationInput Input(PrincipalAuthorizationState state, IEnumerable<RoleGrant> grants, GroupResolutionResult? groups = null, IEnumerable<Role>? possibleRoles = null, NormalizedPrincipal? principal = null) =>
        new(principal ?? new(new("provider", "https://issuer.example", "tenant", PrincipalKind.User, "subject"), new(null, null, null, null)), state, grants, groups ?? GroupResolutionResult.NotApplicable(), possibleRoles ?? []);
}
```

```csharp
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace DataPitcher.UnitTests.Authorization;

public sealed class AuthorizationPropertyTests
{
    [Property(MaxTest = 100)]
    public void AuthorizationEvaluator_IsDeterministic(Role[] roles)
    {
        var input = Input(roles);
        Assert.Equal(AuthorizationEvaluator.Evaluate(input, Permissions.TransfersStart), AuthorizationEvaluator.Evaluate(input, Permissions.TransfersStart));
    }

    [Property(MaxTest = 100)]
    public void AuthorizationEvaluator_WhenAnyPositiveGrantIsRemoved_CanOnlyShrinkEffectivePermissions(Role[] roles, NonNegativeInt index)
    {
        var full = AuthorizationEvaluator.Evaluate(Input(roles), Permissions.TransfersStart).EffectivePermissions;
        var reduced = AuthorizationEvaluator.Evaluate(Input(roles.Where((_, position) => roles.Length == 0 || position != index.Get % roles.Length)), Permissions.TransfersStart).EffectivePermissions;
        Assert.True(reduced.IsSubsetOf(full));
    }

    [Property(MaxTest = 100)]
    public void PermissionSet_WhenAPermissionIsRemoved_CannotGainAccess(Role[] roles, NonNegativeInt index)
    {
        var before = AuthorizationEvaluator.Evaluate(Input(roles), Permissions.TransfersStart).EffectivePermissions;
        var after = before.Permissions.Count == 0 ? before : before.Without(before.Permissions.ElementAt(index.Get % before.Permissions.Count));
        Assert.True(after.IsSubsetOf(before));
    }

    private static AuthorizationInput Input(IEnumerable<Role> roles) =>
        new(new(new("provider", "https://issuer.example", null, PrincipalKind.User, "subject"), new(null, null, null, null)), PrincipalAuthorizationState.Active, roles.Select(role => new RoleGrant(role, RoleGrantSource.ControlDatabaseAssignment)), GroupResolutionResult.NotApplicable(), []);
}
```

- [ ] **Step 2: Run the evaluator tests and confirm the missing authorization model is the failure.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~AuthorizationEvaluatorTests|FullyQualifiedName~AuthorizationPropertyTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'Authorization' does not exist in the namespace 'DataPitcher.Auth.Abstractions'`.

- [ ] **Step 3: Implement the typed evaluator and auditable state-transition value.** Write the following complete production code:

```csharp
// src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationModels.cs
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;

namespace DataPitcher.Auth.Abstractions.Authorization;

public enum RoleGrantSource { ControlDatabaseAssignment, ApplicationRole, DirectoryGroup, OpenIdConnectRole, OpenIdConnectGroup }
public sealed record RoleGrant(Role Role, RoleGrantSource Source);
public enum PrincipalAuthorizationState { Active, TerminalDeny, Disabled }
public enum AuthorizationOutcome { Granted, Denied, Indeterminate }
public sealed record AuthorizationDecision(AuthorizationOutcome Outcome, PermissionSet EffectivePermissions);
public sealed record PrincipalStateChange(ExternalPrincipalKey Principal, PrincipalAuthorizationState PreviousState, PrincipalAuthorizationState CurrentState, ExternalPrincipalKey ChangedBy, DateTimeOffset OccurredAt)
{
    public bool RequiresAudit => (PreviousState is PrincipalAuthorizationState.TerminalDeny or PrincipalAuthorizationState.Disabled) && CurrentState == PrincipalAuthorizationState.Active;
}
public sealed class AuthorizationInput
{
    public AuthorizationInput(NormalizedPrincipal principal, PrincipalAuthorizationState principalState, IEnumerable<RoleGrant> positiveGrants, GroupResolutionResult groupResolution, IEnumerable<Role> rolesThatMayBeGrantedByIndeterminateGroups)
    {
        Principal = principal; PrincipalState = principalState; PositiveGrants = Array.AsReadOnly(positiveGrants.ToArray()); GroupResolution = groupResolution; RolesThatMayBeGrantedByIndeterminateGroups = Array.AsReadOnly(rolesThatMayBeGrantedByIndeterminateGroups.Distinct().ToArray());
    }
    public NormalizedPrincipal Principal { get; }
    public PrincipalAuthorizationState PrincipalState { get; }
    public IReadOnlyCollection<RoleGrant> PositiveGrants { get; }
    public GroupResolutionResult GroupResolution { get; }
    public IReadOnlyCollection<Role> RolesThatMayBeGrantedByIndeterminateGroups { get; }
}
```

```csharp
// src/DataPitcher.Auth.Abstractions/Authorization/AuthorizationEvaluator.cs
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Core.Authorization;

namespace DataPitcher.Auth.Abstractions.Authorization;

public static class AuthorizationEvaluator
{
    public static AuthorizationDecision Evaluate(AuthorizationInput input, Permission requiredPermission)
    {
        if (input.PrincipalState is PrincipalAuthorizationState.TerminalDeny or PrincipalAuthorizationState.Disabled) return new(AuthorizationOutcome.Denied, PermissionSet.Empty);
        var effectivePermissions = EffectivePermissions(input.PositiveGrants);
        if (effectivePermissions.Contains(requiredPermission)) return new(AuthorizationOutcome.Granted, effectivePermissions);
        if (input.GroupResolution.State == GroupResolutionState.Indeterminate && input.RolesThatMayBeGrantedByIndeterminateGroups.Any(role => RoleBundles.For(role).Contains(requiredPermission))) return new(AuthorizationOutcome.Indeterminate, effectivePermissions);
        return new(AuthorizationOutcome.Denied, effectivePermissions);
    }

    public static PermissionSet EffectivePermissions(IEnumerable<RoleGrant> positiveGrants) =>
        positiveGrants.Aggregate(PermissionSet.Empty, (permissions, grant) => permissions.Union(RoleBundles.For(grant.Role)));
}
```

- [ ] **Step 4: Run the evaluator and property tests and confirm all three outcomes and monotonicity properties pass.** Run: `dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --filter "FullyQualifiedName~AuthorizationEvaluatorTests|FullyQualifiedName~AuthorizationPropertyTests"`. Expected: `Passed: 13. Failed: 0.`

- [ ] **Step 5: Run the complete Docker-free unit lane.** Run: `./scripts/test-unit.sh`. Expected: both unit and architecture test runs succeed and the final output reports `Unit lane coverage (informational, not gated): line=100% branch=100% method=100.00%` for the merged covered handwritten code.

- [ ] **Step 6: Commit the provider-neutral evaluator, tests, and test dependency.** Run: `git add src/DataPitcher.Auth.Abstractions/Authorization tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj tests/DataPitcher.UnitTests/Authorization/AuthorizationEvaluatorTests.cs tests/DataPitcher.UnitTests/Authorization/AuthorizationPropertyTests.cs && git commit -m "feat: add deterministic authorization evaluator"`.

## Self-Review

Covered: the plan creates the Core-only Auth.Abstractions boundary; defines the complete permission vocabulary and four fixed bundles; models immutable composite principal identity and separates all four mutable presentation claims; defines all three group-resolution states; evaluates terminal denial before the union of every positive source; distinguishes granted, complete-source denied, and relevant-group indeterminate outcomes; proves union monotonicity with both unit and property tests; and represents disabled-state lifting as an auditable administrative transition.

Deferred: ASP.NET policies and handlers, bearer routing, token validation, provider registration, Entra/OIDC/development normalizers, group-overage network resolution, role-mapping and audit persistence, HTTP 403/503 Problem Details translation, production publish-artifact checks, and Docker integration coverage. No Docker command is required by this slice; `scripts/test-all.sh` remains unchanged as the only merged 100-percent enforcement gate.

Consistency check performed: every later type is introduced in an earlier task or the same task before use; `ExternalPrincipalKey`, `NormalizedPrincipal.AuthorizationKey`, `GroupResolutionResult`, `RoleGrant`, `AuthorizationInput`, `AuthorizationEvaluator.Evaluate`, `AuthorizationEvaluator.EffectivePermissions`, `PermissionSet.Without`, and `PermissionSet.IsSubsetOf` use the same names and signatures throughout.
