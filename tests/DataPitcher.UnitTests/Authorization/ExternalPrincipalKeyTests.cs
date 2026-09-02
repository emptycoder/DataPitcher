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

    [Fact]
    public void ExternalPrincipalKey_ExposesAllImmutableIdentityComponents()
    {
        var key = new ExternalPrincipalKey("entra-prod", "https://login.example/tenant", "tenant-a", PrincipalKind.ServicePrincipal, "object-a");

        Assert.Equal("entra-prod", key.ProviderInstance);
        Assert.Equal("https://login.example/tenant", key.ValidatedIssuer);
        Assert.Equal("tenant-a", key.TenantId);
        Assert.Equal(PrincipalKind.ServicePrincipal, key.PrincipalKind);
        Assert.Equal("object-a", key.ImmutableSubject);
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
