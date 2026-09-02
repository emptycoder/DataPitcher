using System.Security.Claims;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Development;
using DataPitcher.Auth.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void HostingExtensions_WhenNoProvidersAreEnabled_RejectsComposition()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherAuthenticationProviders(configuration, new TestEnvironment { EnvironmentName = "Test" }));
        Assert.Equal("At least one authentication provider must be enabled.", exception.Message);
    }

    [Fact]
    public void HostingExtensions_WhenDevelopmentIsEnabled_RegistersItsNormalizer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Development:Enabled"] = "true",
            ["Authentication:Development:SchemeName"] = "development",
            ["Authentication:Development:ProviderInstance"] = "development",
            ["Authentication:Development:Issuer"] = "https://development.test",
            ["Authentication:Development:Audience"] = "api",
            ["Authentication:Development:SigningKey"] = "01234567890123456789012345678901",
        }).Build();
        services.AddDataPitcherAuthenticationProviders(configuration, new TestEnvironment { EnvironmentName = "Test" });
        using var provider = services.BuildServiceProvider();
        var normalizer = provider.GetRequiredKeyedService<IExternalPrincipalNormalizer>("development");
        var normalized = normalizer.Normalize(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject"), new Claim("roles", "Planner"), new Claim("groups", "group-a") })), "https://development.test");
        Assert.Equal("Planner", normalized.RoleValues.Single());
        Assert.Equal("group-a", normalized.GroupResolution.ImmutableGroupIds.Single());
    }

    [Fact]
    public void HostingExtensions_WhenGenericIsEnabled_RegistersItsNormalizer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Generic:Enabled"] = "true",
            ["Authentication:Generic:SchemeName"] = "generic",
            ["Authentication:Generic:ProviderInstance"] = "generic",
            ["Authentication:Generic:Issuer"] = "https://issuer.test",
            ["Authentication:Generic:Audience"] = "api",
            ["Authentication:Generic:PrincipalKind"] = "User",
        }).Build();
        services.AddDataPitcherAuthenticationProviders(configuration, new TestEnvironment { EnvironmentName = "Test" });
        using var provider = services.BuildServiceProvider();
        var normalizer = provider.GetRequiredKeyedService<IExternalPrincipalNormalizer>("generic");
        var normalized = normalizer.Normalize(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject") })), "https://issuer.test");
        Assert.Equal("subject", normalized.Principal.AuthorizationKey.ImmutableSubject);
    }

    [Fact]
    public void HostingExtensions_WhenEntraIsEnabled_RegistersItsNormalizer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Entra:Enabled"] = "true",
            ["Authentication:Entra:SchemeName"] = "entra",
            ["Authentication:Entra:ProviderInstance"] = "entra",
            ["Authentication:Entra:Instance"] = "https://login.test/",
            ["Authentication:Entra:TenantId"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ["Authentication:Entra:ClientId"] = "client",
        }).Build();
        services.AddDataPitcherAuthenticationProviders(configuration, new TestEnvironment { EnvironmentName = "Test" });
        using var provider = services.BuildServiceProvider();
        var normalizer = provider.GetRequiredKeyedService<IExternalPrincipalNormalizer>("entra");
        var normalized = normalizer.Normalize(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new Claim("idtyp", "user") })), "https://login.test/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0");
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", normalized.Principal.AuthorizationKey.ImmutableSubject);
    }

#if !DEBUG
    [Fact]
    public void HostingExtensions_WhenDevelopmentIsEnabledInRelease_RejectsConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Development:Enabled"] = "true" }).Build();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDataPitcherAuthenticationProviders(configuration, new TestEnvironment()));
        Assert.Equal("Development authentication cannot be enabled in a Release artifact.", exception.Message);
    }
#endif

    private static DevelopmentProviderOptions Options(string signingKey = "01234567890123456789012345678901") => new() { SchemeName = "development", ProviderInstance = "development", Issuer = "https://development.test", Audience = "api", SigningKey = signingKey };
    private sealed class TestEnvironment : IHostEnvironment { public string EnvironmentName { get; set; } = ""; public string ApplicationName { get; set; } = "tests"; public string ContentRootPath { get; set; } = AppContext.BaseDirectory; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider(); }
}
