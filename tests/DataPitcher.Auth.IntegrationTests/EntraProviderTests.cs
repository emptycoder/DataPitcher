using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Auth.Entra;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class EntraProviderTests
{
    [Fact]
    public void EntraNormalizer_MapsValidatedUserAndServicePrincipalKeys()
    {
        var normalizer = new EntraPrincipalNormalizer(
            new EntraProviderOptions
            {
                SchemeName = "entra",
                ProviderInstance = "entra-prod",
                Instance = "https://login.test/",
                TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ClientId = "client",
            }
        );
        var user = normalizer.Normalize(
            Principal(new Claim("idtyp", "user")),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        var app = normalizer.Normalize(
            Principal(new Claim("idtyp", "app")),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        Assert.Equal(PrincipalKind.User, user.Principal.AuthorizationKey.PrincipalKind);
        Assert.Equal(PrincipalKind.ServicePrincipal, app.Principal.AuthorizationKey.PrincipalKind);
    }

    [Fact]
    public void EntraNormalizer_PreservesConfiguredPresentationClaims()
    {
        var normalizer = new EntraPrincipalNormalizer(
            new EntraProviderOptions
            {
                SchemeName = "entra",
                ProviderInstance = "entra-prod",
                Instance = "https://login.test/",
                TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ClientId = "client",
            }
        );
        var normalized = normalizer.Normalize(
            Principal(
                new Claim("idtyp", "user"),
                new Claim("name", "Display Name"),
                new Claim("email", "user@example.test"),
                new Claim("preferred_username", "user"),
                new Claim("upn", "user@tenant.test")
            ),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        Assert.Equal(
            new PrincipalPresentation("Display Name", "user@example.test", "user", "user@tenant.test"),
            normalized.Principal.Presentation
        );
    }

    [Fact]
    public void EntraNormalizer_MatchesOnlyExactOverageClaimNamesAndNeverUsesClaimSourcesEndpoint()
    {
        var normalizer = new EntraPrincipalNormalizer(
            new EntraProviderOptions
            {
                SchemeName = "entra",
                ProviderInstance = "entra-prod",
                Instance = "https://login.test/",
                TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ClientId = "client",
            }
        );
        var claimNames = normalizer.Normalize(
            Principal(
                new Claim("_claim_names", "{\"groups\":\"source\"}"),
                new Claim("_claim_sources", "{\"source\":{\"endpoint\":\"https://attacker.test\"}}")
            ),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        var hasGroups = normalizer.Normalize(
            Principal(new Claim("hasgroups", "true")),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        var malformed = normalizer.Normalize(
            Principal(new Claim("_claim_names", "not-json")),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        var misspelled = normalizer.Normalize(
            Principal(new Claim("_claim_name", "{\"groups\":\"source\"}"), new Claim("hasgroup", "true")),
            "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
        );
        Assert.Equal(GroupResolutionState.Indeterminate, claimNames.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Indeterminate, hasGroups.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Indeterminate, malformed.GroupResolution.State);
        Assert.Equal(GroupResolutionState.Complete, misspelled.GroupResolution.State);
        Assert.Empty(misspelled.GroupResolution.ImmutableGroupIds);
    }

    [Fact]
    public void EntraNormalizer_RejectsAnUnrecognizedIdtypValue()
    {
        var normalizer = new EntraPrincipalNormalizer(
            new EntraProviderOptions
            {
                SchemeName = "entra",
                ProviderInstance = "entra-prod",
                Instance = "https://login.test/",
                TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ClientId = "client",
            }
        );
        Assert.Throws<InvalidOperationException>(() =>
            normalizer.Normalize(
                Principal(new Claim("idtyp", "unknown")),
                "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
            )
        );
    }

    [Fact]
    public void EntraNormalizer_RejectsMissingOrInvalidTenantOrObjectIdentifiers()
    {
        var normalizer = new EntraPrincipalNormalizer(
            new EntraProviderOptions
            {
                SchemeName = "entra",
                ProviderInstance = "entra-prod",
                Instance = "https://login.test/",
                TenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ClientId = "client",
            }
        );
        Assert.Throws<InvalidOperationException>(() =>
            normalizer.Normalize(
                new ClaimsPrincipal(
                    new ClaimsIdentity(new[] { new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") })
                ),
                "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            normalizer.Normalize(
                new ClaimsPrincipal(
                    new ClaimsIdentity(new[] { new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") })
                ),
                "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0"
            )
        );
    }

    [Fact]
    public void EntraProviderRegistration_ValidatesRequiredFieldsAndTenantConfiguration()
    {
        Assert.Throws<ArgumentException>(() =>
            new EntraProviderRegistration(
                Section(
                    new()
                    {
                        ["SchemeName"] = "",
                        ["ProviderInstance"] = "entra-prod",
                        ["Instance"] = "https://login.test/",
                        ["TenantId"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                        ["ClientId"] = "client",
                    }
                )
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new EntraProviderRegistration(
                Section(
                    new()
                    {
                        ["SchemeName"] = "entra",
                        ["ProviderInstance"] = "entra-prod",
                        ["Instance"] = "https://login.test/",
                        ["TenantId"] = "not-a-guid",
                        ["ClientId"] = "client",
                    }
                )
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new EntraProviderRegistration(
                Section(
                    new()
                    {
                        ["SchemeName"] = "entra",
                        ["ProviderInstance"] = "entra-prod",
                        ["Instance"] = "https://login.test/",
                        ["TenantId"] = "organizations",
                        ["ClientId"] = "client",
                    }
                )
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new EntraProviderRegistration(
                Section(
                    new()
                    {
                        ["SchemeName"] = "entra",
                        ["ProviderInstance"] = "entra-prod",
                        ["Instance"] = "https://login.test/",
                        ["TenantId"] = "organizations",
                        ["ClientId"] = "client",
                        ["AllowedTenantIds:0"] = "not-a-guid",
                    }
                )
            )
        );
        var singleTenant = new EntraProviderRegistration(
            Section(
                new()
                {
                    ["SchemeName"] = "entra",
                    ["ProviderInstance"] = "entra-prod",
                    ["Instance"] = "https://login.test/",
                    ["TenantId"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    ["ClientId"] = "client",
                }
            )
        );
        Assert.Equal("entra", singleTenant.SchemeName);
    }

    [Fact]
    public void EntraProviderRegistration_WhenConfigurationSectionIsEmpty_RejectsConfiguration() =>
        Assert.Throws<ArgumentException>(() =>
            new EntraProviderRegistration(new ConfigurationBuilder().Build().GetSection("Authentication:Entra"))
        );

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => "Authentication:Entra:" + pair.Key, pair => pair.Value))
            .Build()
            .GetSection("Authentication:Entra");

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                }.Concat(claims)
            )
        );
}
