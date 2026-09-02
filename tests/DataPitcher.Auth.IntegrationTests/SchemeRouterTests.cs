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
