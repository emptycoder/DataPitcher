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
        var normalizer = new GenericOpenIdConnectPrincipalNormalizer(
            new GenericOpenIdConnectOptions
            {
                SchemeName = "generic",
                ProviderInstance = "generic-prod",
                Issuer = "https://issuer.test",
                Audience = "api",
                PrincipalKind = PrincipalKind.User,
                GroupClaimType = "groups",
            }
        );
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "subject"),
                    new Claim("roles", "Planner"),
                    new Claim("groups", "group-a"),
                    new Claim("scp", "api.read api.write"),
                }
            )
        );
        var normalized = normalizer.Normalize(principal, "https://issuer.test");
        Assert.Equal("https://issuer.test", normalized.Principal.AuthorizationKey.ValidatedIssuer);
        Assert.Equal(PrincipalKind.User, normalized.Principal.AuthorizationKey.PrincipalKind);
        Assert.Equal(new[] { "Planner" }, normalized.RoleValues);
        Assert.Equal(new[] { "group-a" }, normalized.GroupResolution.ImmutableGroupIds);
        Assert.Equal(new[] { "api.read", "api.write" }, normalized.ScopeValues);
    }

    [Fact]
    public void GenericNormalizer_PreservesConfiguredPresentationClaims()
    {
        var normalizer = new GenericOpenIdConnectPrincipalNormalizer(
            new GenericOpenIdConnectOptions
            {
                SchemeName = "generic",
                ProviderInstance = "generic-prod",
                Issuer = "https://issuer.test",
                Audience = "api",
                PrincipalKind = PrincipalKind.User,
            }
        );
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "subject"),
                    new Claim("name", "Display Name"),
                    new Claim("email", "user@example.test"),
                    new Claim("preferred_username", "user"),
                }
            )
        );
        var normalized = normalizer.Normalize(principal, "https://issuer.test");
        Assert.Equal(
            new PrincipalPresentation("Display Name", "user@example.test", "user", null),
            normalized.Principal.Presentation
        );
    }

    [Fact]
    public void GenericNormalizer_RejectsAValidatedTokenWithoutSubject()
    {
        var normalizer = new GenericOpenIdConnectPrincipalNormalizer(
            new GenericOpenIdConnectOptions
            {
                SchemeName = "generic",
                ProviderInstance = "generic-prod",
                Issuer = "https://issuer.test",
                Audience = "api",
                PrincipalKind = PrincipalKind.User,
            }
        );
        var exception = Assert.Throws<InvalidOperationException>(() =>
            normalizer.Normalize(new ClaimsPrincipal(new ClaimsIdentity()), "https://issuer.test")
        );
        Assert.Equal("Validated generic OIDC token has no sub claim.", exception.Message);
    }

    [Fact]
    public void GenericRegistration_WhenRequiredScopeIsMissing_RejectsConfigurationOnlyAtValidationTime()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new GenericOpenIdConnectProviderRegistration(
                Section(
                    new Dictionary<string, string?>
                    {
                        ["SchemeName"] = "generic",
                        ["ProviderInstance"] = "provider",
                        ["Issuer"] = "https://issuer.test",
                        ["Audience"] = "api",
                        ["PrincipalKind"] = "User",
                        ["RequiredScopes:0"] = "",
                    }
                )
            )
        );
        Assert.Equal("Required scopes cannot be empty. (Parameter 'options')", exception.Message);
    }

    [Fact]
    public void GenericRegistration_WhenRequiredFieldsAreMissing_RejectsConfiguration()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new GenericOpenIdConnectProviderRegistration(
                Section(
                    new Dictionary<string, string?>
                    {
                        ["SchemeName"] = "",
                        ["ProviderInstance"] = "provider",
                        ["Issuer"] = "https://issuer.test",
                        ["Audience"] = "api",
                        ["PrincipalKind"] = "User",
                    }
                )
            )
        );
        Assert.Equal(
            "Generic OIDC scheme, provider instance, absolute issuer, and audience are required. (Parameter 'options')",
            exception.Message
        );
    }

    [Fact]
    public void GenericRegistration_WhenConfigurationSectionIsEmpty_RejectsConfiguration() =>
        Assert.Throws<ArgumentException>(() =>
            new GenericOpenIdConnectProviderRegistration(
                new ConfigurationBuilder().Build().GetSection("Authentication:Generic")
            )
        );

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                values.ToDictionary(pair => "Authentication:Generic:" + pair.Key, pair => pair.Value)
            )
            .Build()
            .GetSection("Authentication:Generic");
}
