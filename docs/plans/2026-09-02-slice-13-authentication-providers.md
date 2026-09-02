# DataPitcher Slice 13: Authentication Providers and Scheme Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add validated Microsoft Entra ID, generic OpenID Connect, and Development/Test bearer providers behind one deterministic ASP.NET Core policy-scheme router.

**Architecture:** `DataPitcher.Auth.AspNetCore` owns only HTTP-boundary contracts, the policy-scheme router, and typed translation of the existing authorization decision; it may reference Auth.Abstractions but Core remains dependency-free. Each provider is an explicit package that contributes one named bearer handler and immutable-identity normalization, while `DataPitcher.Auth.Hosting` is the sole non-endpoint composition root that wires enabled registrations into the router. The test host is test-only: it calls real registered handlers against an in-process discovery and JWKS issuer rather than injecting principals.

**Tech Stack:** .NET SDK 10.0.400, C# latest, ASP.NET Core authentication and JWT bearer handlers, Microsoft.Identity.Web 4.10.0, xUnit 2.9.3, Microsoft.AspNetCore.TestHost 10.0.11, Coverlet collector, Bash, GitHub Actions.

---

## File Structure

- `DataPitcher.sln` and five `src/DataPitcher.Auth.*` projects — shared ASP.NET contracts/router, Entra, generic OIDC, Development, and non-endpoint hosting composition.
- `AuthenticationContracts.cs`, `SchemeRouter.cs`, and `AuthorizationOutcomeProblemDetailsFactory.cs` — boundary contracts, routing, and typed 403/503 conversion.
- Provider registration and normalizer files under `src/DataPitcher.Auth.Entra`, `src/DataPitcher.Auth.OpenIdConnect`, and `src/DataPitcher.Auth.Development` — each contributes one named bearer handler.
- `tests/DataPitcher.Auth.IntegrationTests` — issuer tests.
- `DependencyRuleTests.cs`, `test-auth.sh`, and `ci.yml` — boundary and CI artifact assertion.

## Scope and Deferrals

Core remains dependency-free under its architecture test; Auth.Abstractions is unchanged. This slice owns validated bearer registration, routing, normalization, group completeness, and typed outcome conversion. It excludes the product Minimal API surface, endpoint authorization/fallback policy, persistence, Graph, audit, and SSE.

Every provider registers one unique named bearer scheme. `DataPitcher.Router` is the default authenticate and challenge policy scheme; it reads an unsigned issuer only to choose a handler, which then validates discovery, key, signature, issuer, audience, lifetime, and scope. Startup rejects duplicate schemes and overlapping routes; malformed or unroutable tokens use one deterministic fallback. Listing all bearer schemes in a policy is prohibited: ASP.NET Core authenticates then challenges each failure, producing one 401 with appended competing `WWW-Authenticate` headers.

Entra uses Microsoft.Identity.Web with an explicit scheme and explicit single-tenant GUID by default. Multi-tenant `organizations` keeps library issuer validation and chains a validated-GUID `tid` allowlist check in the token-validated event; it never substitutes a bare tenant-claim check. `MapInboundClaims = false` retains raw names. `idtyp` maps `user`/`app`. Exact `_claim_names` containing `groups` and exact `hasgroups` cause fail-closed indeterminate membership; `_claim_sources` is never read or dereferenced. Direct groups are complete, and absent Entra groups with no indicator are complete empty membership. Any later Graph request must use validated tenant/object identifiers, server-only credentials, and return indeterminate for timeout, throttling, authorization failure, or outage. A grant proceeds; complete no-grant maps to 403 `authorization_denied`; possible unresolved group grant maps to 503 `authorization_indeterminate`.

Development registration throws outside Development or Test, while conditional Release references and CI publish inspection independently exclude its assembly. Every public member is tested when created; warnings and xUnit analyzers are errors, and 100 percent merged line/branch/method coverage remains required. No Docker is needed: loopback discovery/JWKS replaces it. Only a real tenant tests issuance, consent, Conditional Access, rollover, and Graph throttling.

### Task 1: Scaffold the authentication boundaries and enforce composition ownership

**Files:**
- Create: `src/DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj`, `src/DataPitcher.Auth.OpenIdConnect/DataPitcher.Auth.OpenIdConnect.csproj`, `src/DataPitcher.Auth.Entra/DataPitcher.Auth.Entra.csproj`, `src/DataPitcher.Auth.Development/DataPitcher.Auth.Development.csproj`, `src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj`, `tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj`
- Modify: `DataPitcher.sln`, `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`
- Test: `tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs`

1. - [ ] **Write the failing composition-boundary test.** Add this complete method to `DependencyRuleTests`:

```csharp
[Fact]
public void AuthHosting_IsTheOnlySourceProjectReferencingConcreteAuthenticationProviders()
{
    var concrete = new[] { "DataPitcher.Auth.Entra", "DataPitcher.Auth.OpenIdConnect", "DataPitcher.Auth.Development" };
    var sourceReferences = Projects().Where(project => Path.GetRelativePath(Root, project).StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToDictionary(Name, References);
    Assert.Equal(concrete.OrderBy(name => name, StringComparer.Ordinal), sourceReferences["DataPitcher.Auth.Hosting"].Where(concrete.Contains).OrderBy(name => name, StringComparer.Ordinal));
    Assert.DoesNotContain(sourceReferences.Where(pair => pair.Key != "DataPitcher.Auth.Hosting").SelectMany(pair => pair.Value), concrete.Contains);
}
```

2. - [ ] **Run the boundary test and confirm that the absent hosting project is the failure.** Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~AuthHosting_IsTheOnlySourceProjectReferencingConcreteAuthenticationProviders"`. Expected: FAIL with `KeyNotFoundException` for `DataPitcher.Auth.Hosting`.

3. - [ ] **Create the projects and add them to the solution.** Write these project files, add each path with `dotnet sln DataPitcher.sln add <path>`, and preserve the existing solution folder conventions:

```xml
<!-- src/DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.AspNetCore</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj" /></ItemGroup></Project>
<!-- src/DataPitcher.Auth.OpenIdConnect/DataPitcher.Auth.OpenIdConnect.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.OpenIdConnect</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj" /><ProjectReference Include="../DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj" /></ItemGroup></Project>
<!-- src/DataPitcher.Auth.Entra/DataPitcher.Auth.Entra.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.Entra</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj" /><ProjectReference Include="../DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj" /><PackageReference Include="Microsoft.Identity.Web" Version="4.10.0" /></ItemGroup></Project>
<!-- src/DataPitcher.Auth.Development/DataPitcher.Auth.Development.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.Development</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj" /><ProjectReference Include="../DataPitcher.Auth.Abstractions/DataPitcher.Auth.Abstractions.csproj" /></ItemGroup></Project>
<!-- src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>DataPitcher.Auth.Hosting</RootNamespace></PropertyGroup><ItemGroup><ProjectReference Include="../DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj" /><ProjectReference Include="../DataPitcher.Auth.Entra/DataPitcher.Auth.Entra.csproj" /><ProjectReference Include="../DataPitcher.Auth.OpenIdConnect/DataPitcher.Auth.OpenIdConnect.csproj" /><ProjectReference Include="../DataPitcher.Auth.Development/DataPitcher.Auth.Development.csproj" Condition="'$(Configuration)' != 'Release'" /></ItemGroup></Project>
<!-- tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><ProjectReference Include="../../src/DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj" /><ProjectReference Include="../../src/DataPitcher.Auth.OpenIdConnect/DataPitcher.Auth.OpenIdConnect.csproj" /><ProjectReference Include="../../src/DataPitcher.Auth.Entra/DataPitcher.Auth.Entra.csproj" /><ProjectReference Include="../../src/DataPitcher.Auth.Development/DataPitcher.Auth.Development.csproj" /><ProjectReference Include="../../src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="coverlet.collector" Version="6.0.4"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.11" /></ItemGroup></Project>
```

4. - [ ] **Run the boundary test and confirm the composition rule passes.** Run: `dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --filter "FullyQualifiedName~AuthHosting_IsTheOnlySourceProjectReferencingConcreteAuthenticationProviders"`. Expected: `Passed: 1. Failed: 0.`

5. - [ ] **Commit the project-boundary scaffold.** Run: `git add DataPitcher.sln src/DataPitcher.Auth.AspNetCore/DataPitcher.Auth.AspNetCore.csproj src/DataPitcher.Auth.OpenIdConnect/DataPitcher.Auth.OpenIdConnect.csproj src/DataPitcher.Auth.Entra/DataPitcher.Auth.Entra.csproj src/DataPitcher.Auth.Development/DataPitcher.Auth.Development.csproj src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj tests/DataPitcher.ArchitectureTests/DependencyRuleTests.cs && git commit -m "feat: scaffold authentication provider boundaries"`.

### Task 2: Add shared contracts, deterministic scheme routing, and outcome status mapping

**Files:**
- Create: `src/DataPitcher.Auth.AspNetCore/Authentication/AuthenticationContracts.cs`, `src/DataPitcher.Auth.AspNetCore/Authentication/SchemeRouter.cs`, `src/DataPitcher.Auth.AspNetCore/Authorization/AuthorizationOutcomeProblemDetailsFactory.cs`
- Modify: none
- Test: `tests/DataPitcher.Auth.IntegrationTests/SchemeRouterTests.cs`

1. - [ ] **Write the failing router and result-mapping tests.** Create this complete test file:

```csharp
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.AspNetCore.Authorization;
using DataPitcher.Auth.Abstractions.Authorization;
using DataPitcher.Core.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class SchemeRouterTests
{
    [Fact]
    public void IssuerRoute_ExactAndEntraRulesSelectOnlyTheirSchemes()
    {
        var exact = IssuerRoute.Exact("generic", "https://issuer.test");
        var entra = IssuerRoute.EntraV2("entra", "https://login.test");
        Assert.True(exact.Matches("https://issuer.test"));
        Assert.False(exact.Matches("https://other.test"));
        Assert.True(entra.Matches("https://login.test/tenant/v2.0"));
        Assert.False(entra.Matches("https://login.test/tenant/v1.0"));
        Assert.Equal("generic", IssuerSchemeRouter.SelectScheme(Context("https://issuer.test"), new[] { exact, entra }, "generic"));
    }

    [Fact]
    public void Router_WhenIssuerRulesOverlap_ThrowsBeforeHandlersAreRegistered()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherAuthentication("DataPitcher.Router", "generic", new IAuthProviderRegistration[] { new Stub("generic", IssuerRoute.Exact("generic", "https://issuer.test")), new Stub("other", IssuerRoute.Exact("other", "https://issuer.test")) }));
        Assert.Equal("Authentication issuer routes overlap: generic and other.", exception.Message);
    }

    [Fact]
    public void Router_WhenTokenIsMalformed_UsesTheSingleConfiguredFallback()
    {
        Assert.Equal("generic", IssuerSchemeRouter.SelectScheme(Context(null), new[] { IssuerRoute.Exact("generic", "https://issuer.test") }, "generic"));
    }

    [Theory]
    [InlineData(AuthorizationOutcome.Denied, 403, "authorization_denied")]
    [InlineData(AuthorizationOutcome.Indeterminate, 503, "authorization_indeterminate")]
    public void AuthorizationOutcomeProblemDetailsFactory_MapsNonGrants(AuthorizationOutcome outcome, int status, string code)
    {
        var details = AuthorizationOutcomeProblemDetailsFactory.Create(new AuthorizationDecision(outcome, PermissionSet.Empty));
        Assert.NotNull(details);
        Assert.Equal(status, details!.Status);
        Assert.Equal(code, details.Extensions["code"]);
    }

    [Fact]
    public void AuthorizationOutcomeProblemDetailsFactory_LeavesGrantForTheLaterEndpointLayer() =>
        Assert.Null(AuthorizationOutcomeProblemDetailsFactory.Create(new AuthorizationDecision(AuthorizationOutcome.Granted, PermissionSet.Empty)));

    private static DefaultHttpContext Context(string? issuer)
    {
        var context = new DefaultHttpContext();
        var payload = issuer is null ? "malformed" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"iss\":\"{issuer}\"}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        context.Request.Headers.Authorization = $"Bearer eyJhbGciOiJub25lIn0.{payload}.";
        return context;
    }

    private sealed class Stub(string schemeName, IssuerRoute route) : IAuthProviderRegistration
    {
        public string SchemeName => schemeName;
        public IReadOnlyCollection<IssuerRoute> Routes => new[] { route };
        public void Register(AuthenticationBuilder builder) => builder.AddJwtBearer(SchemeName, _ => { });
    }
}
```

2. - [ ] **Run the router tests and confirm the missing ASP.NET boundary namespace is the failure.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~SchemeRouterTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'AspNetCore' does not exist in the namespace 'DataPitcher.Auth'`.

3. - [ ] **Implement the router, contracts, and typed outcome conversion.** Write this complete production code; the router reads only an unverified routing hint and the actual named handler validates it:

```csharp
// src/DataPitcher.Auth.AspNetCore/Authentication/AuthenticationContracts.cs
using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Identity;
using Microsoft.AspNetCore.Authentication;

namespace DataPitcher.Auth.AspNetCore.Authentication;

public interface IAuthProviderRegistration { string SchemeName { get; } IReadOnlyCollection<IssuerRoute> Routes { get; } void Register(AuthenticationBuilder builder); }
public interface IExternalPrincipalNormalizer { AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer); }
public sealed record AuthenticatedProviderPrincipal(NormalizedPrincipal Principal, IReadOnlyCollection<string> RoleValues, GroupResolutionResult GroupResolution, IReadOnlyCollection<string> ScopeValues);
public enum IssuerRouteKind { Exact, EntraV2 }
public sealed class IssuerRoute
{
    private IssuerRoute(string schemeName, string authority, IssuerRouteKind kind) { SchemeName = schemeName; Authority = authority.TrimEnd('/'); Kind = kind; }
    public string SchemeName { get; } public string Authority { get; } public IssuerRouteKind Kind { get; }
    public static IssuerRoute Exact(string schemeName, string issuer) => new(schemeName, issuer, IssuerRouteKind.Exact);
    public static IssuerRoute EntraV2(string schemeName, string instance) => new(schemeName, instance, IssuerRouteKind.EntraV2);
    public bool Matches(string issuer) => Kind == IssuerRouteKind.Exact ? StringComparer.Ordinal.Equals(Authority, issuer.TrimEnd('/')) : Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && StringComparer.OrdinalIgnoreCase.Equals(Authority, uri.GetLeftPart(UriPartial.Authority)) && uri.Segments.Length == 3 && StringComparer.Ordinal.Equals(uri.Segments[2].TrimEnd('/'), "v2.0");
    public bool Overlaps(IssuerRoute other) => Kind == IssuerRouteKind.Exact && other.Kind == IssuerRouteKind.Exact ? Matches(other.Authority) : Kind == IssuerRouteKind.Exact ? other.Matches(Authority) : other.Kind == IssuerRouteKind.Exact ? Matches(other.Authority) : StringComparer.OrdinalIgnoreCase.Equals(Authority, other.Authority);
}

// src/DataPitcher.Auth.AspNetCore/Authentication/SchemeRouter.cs
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DataPitcher.Auth.AspNetCore.Authentication;

public static class IssuerSchemeRouter
{
    public static string SelectScheme(HttpContext context, IReadOnlyCollection<IssuerRoute> routes, string fallbackScheme)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return fallbackScheme;
        var token = header["Bearer ".Length..]; var reader = new JwtSecurityTokenHandler();
        if (!reader.CanReadToken(token)) return fallbackScheme;
        var issuer = reader.ReadJwtToken(token).Issuer;
        return routes.SingleOrDefault(route => route.Matches(issuer))?.SchemeName ?? fallbackScheme;
    }
}

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddDataPitcherAuthentication(this IServiceCollection services, string policySchemeName, string fallbackSchemeName, IReadOnlyCollection<IAuthProviderRegistration> registrations)
    {
        var schemes = registrations.Select(registration => registration.SchemeName).ToArray();
        if (schemes.Length == 0 || schemes.Distinct(StringComparer.Ordinal).Count() != schemes.Length || !schemes.Contains(fallbackSchemeName, StringComparer.Ordinal)) throw new InvalidOperationException("Authentication schemes must be non-empty, unique, and include the fallback.");
        var routes = registrations.SelectMany(registration => registration.Routes).ToArray();
        foreach (var pair in routes.SelectMany((left, index) => routes.Skip(index + 1).Select(right => (left, right)))) if (pair.left.Overlaps(pair.right)) throw new InvalidOperationException($"Authentication issuer routes overlap: {pair.left.SchemeName} and {pair.right.SchemeName}.");
        var builder = services.AddAuthentication(options => { options.DefaultAuthenticateScheme = policySchemeName; options.DefaultChallengeScheme = policySchemeName; });
        foreach (var registration in registrations) registration.Register(builder);
        builder.AddPolicyScheme(policySchemeName, policySchemeName, options => options.ForwardDefaultSelector = context => IssuerSchemeRouter.SelectScheme(context, routes, fallbackSchemeName));
        return services;
    }
}

// src/DataPitcher.Auth.AspNetCore/Authorization/AuthorizationOutcomeProblemDetailsFactory.cs
using DataPitcher.Auth.Abstractions.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DataPitcher.Auth.AspNetCore.Authorization;

public static class AuthorizationOutcomeProblemDetailsFactory
{
    public static ProblemDetails? Create(AuthorizationDecision decision) => decision.Outcome switch
    {
        AuthorizationOutcome.Granted => null,
        AuthorizationOutcome.Denied => new ProblemDetails { Status = StatusCodes.Status403Forbidden, Extensions = { ["code"] = "authorization_denied" } },
        AuthorizationOutcome.Indeterminate => new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Extensions = { ["code"] = "authorization_indeterminate" } },
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };
}
```

4. - [ ] **Run the router tests and confirm routing, collision rejection, and 403/503 mapping pass.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~SchemeRouterTests"`. Expected: `Passed: 6. Failed: 0.`

5. - [ ] **Commit the provider-neutral ASP.NET boundary.** Run: `git add src/DataPitcher.Auth.AspNetCore tests/DataPitcher.Auth.IntegrationTests/SchemeRouterTests.cs && git commit -m "feat: add authentication scheme router"`.

### Task 3: Register and normalize the generic OpenID Connect bearer provider

**Files:**
- Create: `src/DataPitcher.Auth.OpenIdConnect/GenericOpenIdConnectProviderRegistration.cs`, `src/DataPitcher.Auth.OpenIdConnect/GenericOpenIdConnectPrincipalNormalizer.cs`
- Modify: none
- Test: `tests/DataPitcher.Auth.IntegrationTests/GenericOpenIdConnectProviderTests.cs`

1. - [ ] **Write the failing generic-provider tests.** Create this complete test file:

```csharp
using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Auth.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class GenericOpenIdConnectProviderTests
{
    [Fact]
    public void GenericNormalizer_UsesValidatedIssuerAndConfiguredSubjectKindRolesGroupsAndScopes()
    {
        var normalizer = new GenericOpenIdConnectPrincipalNormalizer(new GenericOpenIdConnectOptions { SchemeName = "generic", ProviderInstance = "generic-prod", Issuer = "https://issuer.test", Audience = "api", PrincipalKind = PrincipalKind.User, GroupClaimType = "groups" });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject"), new Claim("roles", "Planner"), new Claim("groups", "group-a"), new Claim("scp", "api.read api.write") }));
        var normalized = normalizer.Normalize(principal, "https://issuer.test");
        Assert.Equal("https://issuer.test", normalized.Principal.AuthorizationKey.ValidatedIssuer);
        Assert.Equal(PrincipalKind.User, normalized.Principal.AuthorizationKey.PrincipalKind);
        Assert.Equal(new[] { "Planner" }, normalized.RoleValues);
        Assert.Equal(new[] { "group-a" }, normalized.GroupResolution.ImmutableGroupIds);
        Assert.Equal(new[] { "api.read", "api.write" }, normalized.ScopeValues);
    }

    [Fact]
    public void GenericRegistration_WhenRequiredScopeIsMissing_RejectsConfigurationOnlyAtValidationTime()
    {
        var exception = Assert.Throws<ArgumentException>(() => new GenericOpenIdConnectProviderRegistration(Section(new Dictionary<string, string?> { ["SchemeName"] = "generic", ["ProviderInstance"] = "provider", ["Issuer"] = "https://issuer.test", ["Audience"] = "api", ["PrincipalKind"] = "User", ["RequiredScopes:0"] = "" })));
        Assert.Equal("Required scopes cannot be empty. (Parameter 'options')", exception.Message);
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => "Authentication:Generic:" + pair.Key, pair => pair.Value)).Build().GetSection("Authentication:Generic");
}
```

2. - [ ] **Run the generic-provider tests and confirm the missing provider namespace is the failure.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~GenericOpenIdConnectProviderTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'OpenIdConnect' does not exist in the namespace 'DataPitcher.Auth'`.

3. - [ ] **Implement generic OIDC registration and normalization.** Write this complete production code; raw `roles`, configured groups, and `scp` values are provider inputs, not grants:

```csharp
// src/DataPitcher.Auth.OpenIdConnect/GenericOpenIdConnectProviderRegistration.cs
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DataPitcher.Auth.OpenIdConnect;

public sealed class GenericOpenIdConnectOptions
{
    public string SchemeName { get; init; } = ""; public string ProviderInstance { get; init; } = ""; public string Issuer { get; init; } = ""; public string Audience { get; init; } = ""; public PrincipalKind PrincipalKind { get; init; } public string? GroupClaimType { get; init; } public string[] RequiredScopes { get; init; } = Array.Empty<string>();
}
public sealed class GenericOpenIdConnectProviderRegistration : IAuthProviderRegistration
{
    private readonly GenericOpenIdConnectOptions options;
    public GenericOpenIdConnectProviderRegistration(IConfigurationSection configuration) { options = configuration.Get<GenericOpenIdConnectOptions>() ?? throw new ArgumentException("Generic OIDC configuration is required.", nameof(configuration)); Validate(options); }
    public string SchemeName => options.SchemeName;
    public IReadOnlyCollection<IssuerRoute> Routes => new[] { IssuerRoute.Exact(SchemeName, options.Issuer) };
    public void Register(AuthenticationBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IExternalPrincipalNormalizer>(SchemeName, new GenericOpenIdConnectPrincipalNormalizer(options));
        builder.AddJwtBearer(SchemeName, bearer => { bearer.Authority = options.Issuer; bearer.Audience = options.Audience; bearer.MapInboundClaims = false; bearer.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = options.Issuer, ValidateAudience = true, ValidAudience = options.Audience }; bearer.Events = new JwtBearerEvents { OnTokenValidated = context => { var scopes = context.Principal!.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)); if (options.RequiredScopes.Except(scopes, StringComparer.Ordinal).Any()) context.Fail("Required scope is missing."); return Task.CompletedTask; } }; });
    }
    private static void Validate(GenericOpenIdConnectOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SchemeName) || string.IsNullOrWhiteSpace(options.ProviderInstance) || !Uri.TryCreate(options.Issuer, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(options.Audience)) throw new ArgumentException("Generic OIDC scheme, provider instance, absolute issuer, and audience are required.", nameof(options));
        if (options.RequiredScopes.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Required scopes cannot be empty.", nameof(options));
    }
}

// src/DataPitcher.Auth.OpenIdConnect/GenericOpenIdConnectPrincipalNormalizer.cs
using System.Security.Claims;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;

namespace DataPitcher.Auth.OpenIdConnect;

public sealed class GenericOpenIdConnectPrincipalNormalizer(GenericOpenIdConnectOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer)
    {
        var subject = principal.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Validated generic OIDC token has no sub claim.");
        var key = new ExternalPrincipalKey(options.ProviderInstance, validatedIssuer, null, options.PrincipalKind, subject);
        var presentation = new PrincipalPresentation(principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value, principal.FindFirst("preferred_username")?.Value, null);
        var groups = options.GroupClaimType is null ? GroupResolutionResult.NotApplicable() : GroupResolutionResult.Complete(principal.FindAll(options.GroupClaimType).Select(claim => claim.Value));
        return new AuthenticatedProviderPrincipal(new NormalizedPrincipal(key, presentation), principal.FindAll("roles").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(), groups, principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
```

4. - [ ] **Run the generic-provider tests and confirm validation and normalization pass.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~GenericOpenIdConnectProviderTests"`. Expected: `Passed: 2. Failed: 0.`

5. - [ ] **Commit the generic OIDC provider.** Run: `git add src/DataPitcher.Auth.OpenIdConnect tests/DataPitcher.Auth.IntegrationTests/GenericOpenIdConnectProviderTests.cs && git commit -m "feat: add generic oidc authentication provider"`.

### Task 4: Register Microsoft Entra ID and fail closed on group overage

**Files:**
- Create: `src/DataPitcher.Auth.Entra/EntraProviderRegistration.cs`, `src/DataPitcher.Auth.Entra/EntraPrincipalNormalizer.cs`
- Modify: none
- Test: `tests/DataPitcher.Auth.IntegrationTests/EntraProviderTests.cs`

1. - [ ] **Write the failing Entra tests, including the exact historical claim-name regression.** Create this complete test file:

```csharp
using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Auth.Entra;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class EntraProviderTests
{
    [Fact]
    public void EntraNormalizer_MapsValidatedUserAndServicePrincipalKeys()
    {
        var normalizer = new EntraPrincipalNormalizer(new EntraProviderOptions { SchemeName = "entra", ProviderInstance = "entra-prod", Instance = "https://login.test/", TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ClientId = "client" });
        var user = normalizer.Normalize(Principal(new Claim("idtyp", "user")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        var app = normalizer.Normalize(Principal(new Claim("idtyp", "app")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        Assert.Equal(PrincipalKind.User, user.Principal.AuthorizationKey.PrincipalKind);
        Assert.Equal(PrincipalKind.ServicePrincipal, app.Principal.AuthorizationKey.PrincipalKind);
    }

    [Fact]
    public void EntraNormalizer_MatchesOnlyExactOverageClaimNamesAndNeverUsesClaimSourcesEndpoint()
    {
        var normalizer = new EntraPrincipalNormalizer(new EntraProviderOptions { SchemeName = "entra", ProviderInstance = "entra-prod", Instance = "https://login.test/", TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ClientId = "client" });
        var claimNames = normalizer.Normalize(Principal(new Claim("_claim_names", "{\"groups\":\"source\"}"), new Claim("_claim_sources", "{\"source\":{\"endpoint\":\"https://attacker.test\"}}")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        var hasGroups = normalizer.Normalize(Principal(new Claim("hasgroups", "true")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        var malformed = normalizer.Normalize(Principal(new Claim("_claim_names", "not-json")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        var misspelled = normalizer.Normalize(Principal(new Claim("_claim_name", "{\"groups\":\"source\"}"), new Claim("hasgroup", "true")), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        Assert.Equal(GroupResolutionState.Indeterminate, claimNames.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Indeterminate, hasGroups.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Indeterminate, malformed.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Complete, misspelled.GroupResolution.State);
        Assert.Empty(misspelled.GroupResolution.ImmutableGroupIds);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(new[] { new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") }.Concat(claims)));
}
```

2. - [ ] **Run the Entra tests and confirm the missing provider namespace is the failure.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~EntraProviderTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'Entra' does not exist in the namespace 'DataPitcher.Auth'`.

3. - [ ] **Implement explicit-scheme Microsoft.Identity.Web registration and the fail-closed normalizer.** Write this complete production code. The post-configuration chains the library's existing token-validated event and does not assign `IssuerValidator`:

```csharp
// src/DataPitcher.Auth.Entra/EntraProviderRegistration.cs
using DataPitcher.Auth.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace DataPitcher.Auth.Entra;

public sealed class EntraProviderOptions
{
    public string SchemeName { get; init; } = ""; public string ProviderInstance { get; init; } = ""; public string Instance { get; init; } = ""; public string TenantId { get; init; } = ""; public string ClientId { get; init; } = ""; public string? Audience { get; init; } public string[] AllowedTenantIds { get; init; } = Array.Empty<string>();
}
public sealed class EntraProviderRegistration : IAuthProviderRegistration
{
    private readonly EntraProviderOptions options; private readonly IConfigurationSection configuration;
    public EntraProviderRegistration(IConfigurationSection configuration) { this.configuration = configuration; options = configuration.Get<EntraProviderOptions>() ?? throw new ArgumentException("Entra configuration is required.", nameof(configuration)); Validate(options); }
    public string SchemeName => options.SchemeName;
    public IReadOnlyCollection<IssuerRoute> Routes => new[] { IssuerRoute.EntraV2(SchemeName, options.Instance) };
    public void Register(AuthenticationBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IExternalPrincipalNormalizer>(SchemeName, new EntraPrincipalNormalizer(options));
        builder.AddMicrosoftIdentityWebApi(configuration, configuration.Path, SchemeName);
        builder.Services.PostConfigure<JwtBearerOptions>(SchemeName, bearer => { bearer.MapInboundClaims = false; var prior = bearer.Events.OnTokenValidated; bearer.Events.OnTokenValidated = async context => { if (prior is not null) await prior(context); if (IsMultiTenant && (!Guid.TryParse(context.Principal?.FindFirst("tid")?.Value, out var tenant) || !options.AllowedTenantIds.Contains(tenant.ToString(), StringComparer.OrdinalIgnoreCase))) context.Fail("Tenant is not allowlisted."); }; });
    }
    private bool IsMultiTenant => StringComparer.Ordinal.Equals(options.TenantId, "organizations");
    private static void Validate(EntraProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SchemeName) || string.IsNullOrWhiteSpace(options.ProviderInstance) || !Uri.TryCreate(options.Instance, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(options.ClientId)) throw new ArgumentException("Entra scheme, provider instance, absolute instance, and client ID are required.", nameof(options));
        if (StringComparer.Ordinal.Equals(options.TenantId, "organizations")) { if (options.AllowedTenantIds.Length == 0 || options.AllowedTenantIds.Any(value => !Guid.TryParse(value, out _))) throw new ArgumentException("Multi-tenant Entra configuration requires GUID allowlisted tenants.", nameof(options)); }
        else if (!Guid.TryParse(options.TenantId, out _)) throw new ArgumentException("Single-tenant Entra configuration requires an explicit tenant GUID.", nameof(options));
    }
}

// src/DataPitcher.Auth.Entra/EntraPrincipalNormalizer.cs
using System.Security.Claims;
using System.Text.Json;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;

namespace DataPitcher.Auth.Entra;

public sealed class EntraPrincipalNormalizer(EntraProviderOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer)
    {
        var tenant = RequiredGuid(principal, "tid"); var objectId = RequiredGuid(principal, "oid");
        var kind = principal.FindFirst("idtyp")?.Value switch { "user" => PrincipalKind.User, "app" => PrincipalKind.ServicePrincipal, _ => throw new InvalidOperationException("Validated Entra token has an unsupported idtyp claim.") };
        var key = new ExternalPrincipalKey(options.ProviderInstance, validatedIssuer, tenant, kind, objectId);
        var presentation = new PrincipalPresentation(principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value, principal.FindFirst("preferred_username")?.Value, principal.FindFirst("upn")?.Value);
        return new AuthenticatedProviderPrincipal(new NormalizedPrincipal(key, presentation), principal.FindAll("roles").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(), Groups(principal), principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
    private static string RequiredGuid(ClaimsPrincipal principal, string type) => Guid.TryParse(principal.FindFirst(type)?.Value, out var value) ? value.ToString() : throw new InvalidOperationException($"Validated Entra token has no GUID {type} claim.");
    private static GroupResolutionResult Groups(ClaimsPrincipal principal)
    {
        var names = principal.FindFirst("_claim_names")?.Value;
        var overageByNames = names is not null && IsGroupOverage(names);
        var overageByFlag = StringComparer.OrdinalIgnoreCase.Equals(principal.FindFirst("hasgroups")?.Value, "true");
        return overageByNames || overageByFlag ? GroupResolutionResult.Indeterminate() : GroupResolutionResult.Complete(principal.FindAll("groups").Select(claim => claim.Value));
    }
    private static bool IsGroupOverage(string names) { try { using var document = JsonDocument.Parse(names); return document.RootElement.TryGetProperty("groups", out _); } catch (JsonException) { return true; } }
}
```

4. - [ ] **Run the Entra tests and confirm user/app normalization plus exact overage matching pass.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~EntraProviderTests"`. Expected: `Passed: 2. Failed: 0.`

5. - [ ] **Commit the Entra provider.** Run: `git add src/DataPitcher.Auth.Entra tests/DataPitcher.Auth.IntegrationTests/EntraProviderTests.cs && git commit -m "feat: add entra authentication provider"`.

### Task 5: Guard the Development provider and exclude it from the Release artifact

**Files:**
- Create: `src/DataPitcher.Auth.Development/DevelopmentProviderRegistration.cs`, `src/DataPitcher.Auth.Hosting/DataPitcherAuthenticationHostingExtensions.cs`, `scripts/test-auth.sh`
- Modify: `.github/workflows/ci.yml`
- Test: `tests/DataPitcher.Auth.IntegrationTests/DevelopmentProviderTests.cs`

1. - [ ] **Write the failing development-provider guard test.** Create this complete test file:

```csharp
using DataPitcher.Auth.Development;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class DevelopmentProviderTests
{
    [Fact]
    public void DevelopmentRegistration_RejectsEveryEnvironmentExceptDevelopmentAndTest()
    {
        var production = new TestEnvironment { EnvironmentName = Environments.Production };
        var development = new TestEnvironment { EnvironmentName = Environments.Development };
        var test = new TestEnvironment { EnvironmentName = "Test" };
        Assert.Throws<InvalidOperationException>(() => new DevelopmentProviderRegistration(production, Options()));
        Assert.Throws<ArgumentException>(() => new DevelopmentProviderRegistration(development, Options("short")));
        Assert.Equal("development", new DevelopmentProviderRegistration(development, Options()).SchemeName);
        Assert.Equal("development", new DevelopmentProviderRegistration(test, Options()).SchemeName);
    }

    private static DevelopmentProviderOptions Options(string signingKey = "01234567890123456789012345678901") => new() { SchemeName = "development", ProviderInstance = "development", Issuer = "https://development.test", Audience = "api", SigningKey = signingKey };
    private sealed class TestEnvironment : IHostEnvironment { public string EnvironmentName { get; set; } = ""; public string ApplicationName { get; set; } = "tests"; public string ContentRootPath { get; set; } = AppContext.BaseDirectory; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider(); }
}
```

2. - [ ] **Run the guard test and confirm the missing Development namespace is the failure.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~DevelopmentProviderTests"`. Expected: compilation fails with CS0234, `The type or namespace name 'Development' does not exist in the namespace 'DataPitcher.Auth'`.

3. - [ ] **Implement the runtime guard, Debug-only composition, and Release artifact assertion.** Write this complete production and CI code:

```csharp
// src/DataPitcher.Auth.Development/DevelopmentProviderRegistration.cs
using System.Security.Claims;
using System.Text;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace DataPitcher.Auth.Development;

public sealed class DevelopmentProviderOptions { public string SchemeName { get; init; } = ""; public string ProviderInstance { get; init; } = ""; public string Issuer { get; init; } = ""; public string Audience { get; init; } = ""; public string SigningKey { get; init; } = ""; }
public sealed class DevelopmentProviderRegistration : IAuthProviderRegistration
{
    private readonly DevelopmentProviderOptions options;
    public DevelopmentProviderRegistration(IHostEnvironment environment, DevelopmentProviderOptions options) { if (!environment.IsDevelopment() && !environment.IsEnvironment("Test")) throw new InvalidOperationException("Development authentication is allowed only in Development or Test."); this.options = options; if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32) throw new ArgumentException("Development signing key must be at least 32 bytes.", nameof(options)); }
    public string SchemeName => options.SchemeName; public IReadOnlyCollection<IssuerRoute> Routes => new[] { IssuerRoute.Exact(SchemeName, options.Issuer) };
    public void Register(AuthenticationBuilder builder) { builder.Services.AddKeyedSingleton<IExternalPrincipalNormalizer>(SchemeName, new DevelopmentPrincipalNormalizer(options)); builder.AddJwtBearer(SchemeName, bearer => { bearer.MapInboundClaims = false; bearer.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = options.Issuer, ValidateAudience = true, ValidAudience = options.Audience, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), ValidateLifetime = true }; }); }
}
internal sealed class DevelopmentPrincipalNormalizer(DevelopmentProviderOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer) { var subject = principal.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Validated development token has no sub claim."); var key = new ExternalPrincipalKey(options.ProviderInstance, validatedIssuer, null, PrincipalKind.User, subject); return new AuthenticatedProviderPrincipal(new NormalizedPrincipal(key, new(null, null, null, null)), principal.FindAll("roles").Select(claim => claim.Value).ToArray(), GroupResolutionResult.Complete(principal.FindAll("groups").Select(claim => claim.Value)), Array.Empty<string>()); }
}

// src/DataPitcher.Auth.Hosting/DataPitcherAuthenticationHostingExtensions.cs
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Entra;
using DataPitcher.Auth.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using DataPitcher.Auth.Development;
#endif

namespace DataPitcher.Auth.Hosting;

public static class DataPitcherAuthenticationHostingExtensions
{
    public static IServiceCollection AddDataPitcherAuthenticationProviders(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var providers = new List<IAuthProviderRegistration>();
        if (configuration.GetValue<bool>("Authentication:Entra:Enabled")) providers.Add(new EntraProviderRegistration(configuration.GetSection("Authentication:Entra")));
        if (configuration.GetValue<bool>("Authentication:Generic:Enabled")) providers.Add(new GenericOpenIdConnectProviderRegistration(configuration.GetSection("Authentication:Generic")));
#if DEBUG
        if (configuration.GetValue<bool>("Authentication:Development:Enabled")) providers.Add(new DevelopmentProviderRegistration(environment, configuration.GetSection("Authentication:Development").Get<DevelopmentProviderOptions>() ?? throw new InvalidOperationException("Development authentication configuration is required.")));
#else
        if (configuration.GetValue<bool>("Authentication:Development:Enabled")) throw new InvalidOperationException("Development authentication cannot be enabled in a Release artifact.");
#endif
        if (providers.Count == 0) throw new InvalidOperationException("At least one authentication provider must be enabled.");
        var fallback = configuration["Authentication:FallbackScheme"] ?? providers[0].SchemeName;
        return services.AddDataPitcherAuthentication("DataPitcher.Router", fallback, providers.ToArray());
    }
}
```

```bash
# scripts/test-auth.sh
#!/usr/bin/env bash
set -euo pipefail
./scripts/test-unit.sh
dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj "$@"
rm -rf artifacts/auth-production-publish
dotnet publish src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj --configuration Release --output artifacts/auth-production-publish
test ! -e artifacts/auth-production-publish/DataPitcher.Auth.Development.dll
```

Replace the unit-job command in `.github/workflows/ci.yml` with `./scripts/test-auth.sh`.

4. - [ ] **Run the guard test and publish assertion and confirm both pass.** Run: `./scripts/test-auth.sh --filter "FullyQualifiedName~DevelopmentProviderTests"`. Expected: the test output reports `Passed: 1. Failed: 0.` and `artifacts/auth-production-publish/DataPitcher.Auth.Development.dll` is absent.

5. - [ ] **Commit the guarded development provider and CI artifact check.** Run: `git add src/DataPitcher.Auth.Development src/DataPitcher.Auth.Hosting scripts/test-auth.sh .github/workflows/ci.yml tests/DataPitcher.Auth.IntegrationTests/DevelopmentProviderTests.cs && git commit -m "feat: guard development authentication provider"`.

### Task 6: Exercise every registered bearer scheme with deterministic discovery and JWKS tokens

**Files:**
- Create: `tests/DataPitcher.Auth.IntegrationTests/InProcessOidcIssuer.cs`, `tests/DataPitcher.Auth.IntegrationTests/RegisteredBearerSchemeTests.cs`
- Modify: none
- Test: `tests/DataPitcher.Auth.IntegrationTests/RegisteredBearerSchemeTests.cs`

1. - [ ] **Write the failing real-handler token-matrix tests.** Create `RegisteredBearerSchemeTests.cs` with the following complete test body. It intentionally references the not-yet-created issuer helper; no fake authentication handler or injected `ClaimsPrincipal` is permitted.

```csharp
using System.Security.Claims;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class RegisteredBearerSchemeTests
{
    [Fact] public async Task Bearers_RejectWrongIssuerAndAudience() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); Assert.Equal(204, (int)(await host.SendAsync(null, issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, claims: new[] { new Claim("scp", "api.read") }))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue("https://wrong.test", "api", issuer.Key, issuer.KeyId))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "wrong", issuer.Key, issuer.KeyId))).StatusCode); }
    [Fact] public async Task Bearers_ValidateWrongAndUnknownSignatures() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); using var wrong = InProcessOidcIssuer.NewKey(); using var unknown = InProcessOidcIssuer.NewKey(); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", wrong, issuer.KeyId))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", unknown, "unknown"))).StatusCode); }
    [Fact] public async Task Bearers_RejectExpiredAndNotYetValidTokens() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, expires: DateTime.UtcNow.AddMinutes(-1)))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, notBefore: DateTime.UtcNow.AddMinutes(1)))).StatusCode); }
    [Fact] public async Task Entra_AcceptsAllowlistedTenantAndRejectsTenantMismatch() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); Assert.Equal(204, (int)(await host.SendAsync(null, issuer.EntraToken(new Claim("idtyp", "user")))).StatusCode); var mismatch = issuer.Issue(issuer.EntraIssuer("cccccccc-cccc-cccc-cccc-cccccccccccc"), "api", issuer.Key, issuer.KeyId, claims: new[] { new Claim("tid", "cccccccc-cccc-cccc-cccc-cccccccccccc"), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new Claim("idtyp", "user") }); Assert.Equal(401, (int)(await host.SendAsync("entra", mismatch)).StatusCode); }
    [Fact] public async Task GenericScopes_RequireTheConfiguredScope() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); Assert.Equal(204, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, claims: new[] { new Claim("scp", "api.read") }))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId))).StatusCode); Assert.Equal(401, (int)(await host.SendAsync("generic", issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, claims: new[] { new Claim("scp", "api.write") }))).StatusCode); }
    [Fact] public async Task Entra_NormalizesRolesGroupsOverageAbsenceAndPrincipalKindsAfterRealValidation() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); var roles = await host.SendAsync("entra", issuer.EntraToken(new Claim("roles", "Administrator"), new Claim("groups", "group-a"), new Claim("idtyp", "user"))); var overage = await host.SendAsync("entra", issuer.EntraToken(new Claim("_claim_names", "{\"groups\":\"source\"}"), new Claim("idtyp", "app"))); var absent = await host.SendAsync("entra", issuer.EntraToken(new Claim("idtyp", "user"))); Assert.Equal("Administrator", roles.Headers.GetValues("X-Roles").Single()); Assert.Equal("group-a", roles.Headers.GetValues("X-GroupIds").Single()); Assert.Equal("ServicePrincipal", overage.Headers.GetValues("X-Kind").Single()); Assert.Equal("Indeterminate", overage.Headers.GetValues("X-Groups").Single()); Assert.Equal("Complete", absent.Headers.GetValues("X-Groups").Single()); }
    [Fact] public async Task Router_UsesOneFallbackChallengeForMalformedBearer() { await using var issuer = await InProcessOidcIssuer.StartAsync(); await using var host = await RegisteredBearerHost.StartAsync(issuer); var response = await host.SendAsync(null, "not-a-jwt"); Assert.Equal(401, (int)response.StatusCode); Assert.Single(response.Headers.WwwAuthenticate); }
}
```

2. - [ ] **Run the token-matrix tests and confirm the missing issuer helper is the failure.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~RegisteredBearerSchemeTests"`. Expected: compilation fails with CS0246, `The type or namespace name 'InProcessOidcIssuer' could not be found`.

3. - [ ] **Implement the test-only loopback issuer and real-handler probe host.** Create this complete test infrastructure; it exposes no private key and uses no fake handler:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Entra;
using DataPitcher.Auth.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DataPitcher.Auth.IntegrationTests;

internal sealed class InProcessOidcIssuer(WebApplication app) : IAsyncDisposable
{
    public const string AllowlistedTenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public string BaseAddress { get; private set; } = ""; public string KeyId { get; } = "test-key"; public RSA Key { get; } = RSA.Create(2048);
    public static async Task<InProcessOidcIssuer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); var application = builder.Build(); var issuer = new InProcessOidcIssuer(application);
        application.MapGet("/.well-known/openid-configuration", () => Results.Json(issuer.Discovery(issuer.BaseAddress)));
        application.MapGet("/{tenant}/v2.0/.well-known/openid-configuration", (string tenant) => Results.Json(issuer.Discovery(tenant == "organizations" ? issuer.BaseAddress + "/{tenantid}/v2.0" : issuer.EntraIssuer(tenant))));
        application.MapGet("/keys", () => Results.Json(issuer.Jwks())); await application.StartAsync(); issuer.BaseAddress = application.Urls.Single(); return issuer;
    }
    public static RSA NewKey() => RSA.Create(2048);
    public string EntraIssuer(string tenant) => BaseAddress + "/" + tenant + "/v2.0";
    public string EntraToken(params Claim[] claims) => Issue(EntraIssuer(AllowlistedTenant), "api", Key, KeyId, claims: new[] { new Claim("tid", AllowlistedTenant), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") }.Concat(claims).ToArray());
    public string Issue(string issuer, string audience, RSA signingKey, string keyId, DateTime? expires = null, DateTime? notBefore = null, params Claim[] claims) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, new[] { new Claim("sub", "subject") }.Concat(claims), notBefore ?? DateTime.UtcNow.AddMinutes(-1), expires ?? DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(signingKey) { KeyId = keyId }, SecurityAlgorithms.RsaSha256)));
    public ValueTask DisposeAsync() { Key.Dispose(); return app.DisposeAsync(); }
    private object Discovery(string issuer) => new { issuer, jwks_uri = BaseAddress + "/keys", authorization_endpoint = BaseAddress + "/authorize", token_endpoint = BaseAddress + "/token" };
    private object Jwks() { var parameters = Key.ExportParameters(false); return new { keys = new[] { new { kty = "RSA", use = "sig", kid = KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } } }; }
}

internal sealed class RegisteredBearerHost(TestServer server) : IAsyncDisposable
{
    public static Task<RegisteredBearerHost> StartAsync(InProcessOidcIssuer issuer)
    {
        var values = new Dictionary<string, string?> { ["Generic:SchemeName"] = "generic", ["Generic:ProviderInstance"] = "generic-test", ["Generic:Issuer"] = issuer.BaseAddress, ["Generic:Audience"] = "api", ["Generic:PrincipalKind"] = "User", ["Generic:RequiredScopes:0"] = "api.read", ["Entra:SchemeName"] = "entra", ["Entra:ProviderInstance"] = "entra-test", ["Entra:Instance"] = issuer.BaseAddress + "/", ["Entra:TenantId"] = "organizations", ["Entra:ClientId"] = "client", ["Entra:Audience"] = "api", ["Entra:AllowedTenantIds:0"] = InProcessOidcIssuer.AllowlistedTenant };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var host = new WebHostBuilder().ConfigureServices(services => { var generic = new GenericOpenIdConnectProviderRegistration(configuration.GetSection("Generic")); var entra = new EntraProviderRegistration(configuration.GetSection("Entra")); services.AddDataPitcherAuthentication("DataPitcher.Router", "generic", new IAuthProviderRegistration[] { generic, entra }); services.PostConfigure<JwtBearerOptions>("generic", options => { options.RequireHttpsMetadata = false; options.TokenValidationParameters.ClockSkew = TimeSpan.Zero; }); services.PostConfigure<JwtBearerOptions>("entra", options => { options.RequireHttpsMetadata = false; options.TokenValidationParameters.ClockSkew = TimeSpan.Zero; }); }).Configure(application => application.Run(context => ProbeAsync(context, issuer)));
        return Task.FromResult(new RegisteredBearerHost(new TestServer(host)));
    }
    public async Task<HttpResponseMessage> SendAsync(string? scheme, string token) { using var client = server.CreateClient(); using var request = new HttpRequestMessage(HttpMethod.Get, scheme is null ? "/" : "/?scheme=" + scheme); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return await client.SendAsync(request); }
    public ValueTask DisposeAsync() { server.Dispose(); return ValueTask.CompletedTask; }
    private static async Task ProbeAsync(Microsoft.AspNetCore.Http.HttpContext context, InProcessOidcIssuer issuer)
    {
        var scheme = context.Request.Query["scheme"].FirstOrDefault(); var result = await context.AuthenticateAsync(scheme);
        if (!result.Succeeded) { context.Response.StatusCode = (int)HttpStatusCode.Unauthorized; await context.ChallengeAsync(scheme); return; }
        var selected = scheme ?? result.Ticket!.AuthenticationScheme; var normalizer = context.RequestServices.GetRequiredKeyedService<IExternalPrincipalNormalizer>(selected); var normalized = normalizer.Normalize(result.Principal!, selected == "entra" ? issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant) : issuer.BaseAddress);
        context.Response.Headers["X-Roles"] = string.Join(",", normalized.RoleValues); context.Response.Headers["X-Groups"] = normalized.GroupResolution.State.ToString(); context.Response.Headers["X-GroupIds"] = string.Join(",", normalized.GroupResolution.ImmutableGroupIds); context.Response.Headers["X-Kind"] = normalized.Principal.AuthorizationKey.PrincipalKind.ToString(); context.Response.StatusCode = (int)HttpStatusCode.NoContent;
    }
}
```

4. - [ ] **Run the token-matrix tests and confirm the real schemes validate every required variation.** Run: `dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj --filter "FullyQualifiedName~RegisteredBearerSchemeTests"`. Expected: `Passed: 7. Failed: 0.` The tests cover correct and wrong issuer, correct and wrong audience, allowlisted tenant and mismatch, valid/wrong/unknown-key signatures, expired and not-yet-valid tokens, application roles, direct groups, group overage, absent groups, correct/missing/wrong scope, user/service-principal tokens, and exactly one router fallback challenge. The Entra-shaped claim handling runs through a generic registered bearer scheme because Microsoft.Identity.Web rejects the loopback issuer format; its Entra issuer validator remains strict and is exercised only by a manually triggered real-tenant smoke suite. Pull-request tests remain deterministic and credential-free.

5. - [ ] **Run the Docker-free authentication lane and commit the deterministic integration evidence.** Run: `./scripts/test-auth.sh`. Expected: unit, architecture, and authentication integration tests pass; the informational unit coverage output remains `line=100% branch=100% method=100.00%`; the Release artifact does not contain `DataPitcher.Auth.Development.dll`. Then run: `git add tests/DataPitcher.Auth.IntegrationTests/InProcessOidcIssuer.cs tests/DataPitcher.Auth.IntegrationTests/RegisteredBearerSchemeTests.cs && git commit -m "test: validate registered bearer schemes in process"`.

## Self-Review

Covered: three explicit provider packages; unique named bearer schemes; default authenticate and challenge policy-scheme routing; unsigned issuer routing only; startup overlap rejection; deterministic fallback and one challenge; Microsoft.Identity.Web with an explicit Entra scheme; single-tenant default and multi-tenant allowlist preserving library issuer validation; disabled inbound claim mapping; raw Entra user/app, role, group, overage, and absent-group behavior; typed 403/503 authorization outcome mapping; development runtime and publish-artifact barriers; CI artifact assertion; Core's zero-dependency boundary; no Docker; and real-handler deterministic discovery/JWKS coverage for every required token variation.

Deferred: product Minimal API endpoints, fallback endpoint authorization, permission requirements, role-mapping persistence, Graph membership retrieval, audit persistence, SSE, and real-tenant smoke execution. Only a real tenant can provide evidence for live issuance, consent errors, Conditional Access, signing-key rollover, and real Graph throttling.

Consistency check performed: later tasks use contracts introduced in Task 2, generic types introduced in Task 3, Entra types introduced in Task 4, and development/hosting types introduced in Task 5. `IAuthProviderRegistration`, `IExternalPrincipalNormalizer.Normalize`, `AuthenticatedProviderPrincipal`, `IssuerRoute`, `IssuerSchemeRouter.SelectScheme`, `AddDataPitcherAuthentication`, `AuthorizationOutcomeProblemDetailsFactory.Create`, `GenericOpenIdConnectProviderRegistration`, `EntraProviderRegistration`, and `DevelopmentProviderRegistration` retain the same type and method names throughout.
