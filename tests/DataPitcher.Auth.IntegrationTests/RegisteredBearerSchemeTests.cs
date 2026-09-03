using System.Net;
using System.Security.Claims;
using Xunit;

namespace DataPitcher.Auth.IntegrationTests;

public sealed class RegisteredBearerSchemeTests
{
    [Fact]
    public async Task GenericBearer_AcceptsATokenMatchingAllConfiguredValidationRequirements()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            null,
            issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, claims: [new Claim("scp", "api.read")])
        );
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            response.Headers.TryGetValues("X-Authentication-Failure", out var failures)
                ? failures.Single()
                : "The bearer handler rejected the token without a validation failure."
        );
    }

    [Fact]
    public async Task GenericBearer_RejectsAWrongIssuer()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue("https://wrong.test", "api", issuer.Key, issuer.KeyId, claims: [new Claim("scp", "api.read")])
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAWrongAudience()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(issuer.BaseAddress, "wrong", issuer.Key, issuer.KeyId, claims: [new Claim("scp", "api.read")])
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAWrongSignature()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        using var wrongKey = InProcessOidcIssuer.NewKey();
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(issuer.BaseAddress, "api", wrongKey, issuer.KeyId, claims: [new Claim("scp", "api.read")])
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAnUnknownSigningKey()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        using var unknownKey = InProcessOidcIssuer.NewKey();
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(issuer.BaseAddress, "api", unknownKey, "unknown", claims: [new Claim("scp", "api.read")])
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAnExpiredToken()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(
                issuer.BaseAddress,
                "api",
                issuer.Key,
                issuer.KeyId,
                expires: DateTime.UtcNow.AddMinutes(-1),
                claims: [new Claim("scp", "api.read")]
            )
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsANotYetValidToken()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(
                issuer.BaseAddress,
                "api",
                issuer.Key,
                issuer.KeyId,
                notBefore: DateTime.UtcNow.AddMinutes(1),
                claims: [new Claim("scp", "api.read")]
            )
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAMissingRequiredScope()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericBearer_RejectsAWrongScope()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(
            "generic",
            issuer.Issue(issuer.BaseAddress, "api", issuer.Key, issuer.KeyId, claims: [new Claim("scp", "api.write")])
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EntraShapedClaims_AcceptAnAllowlistedTenantAfterGenericBearerValidation()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync(null, issuer.EntraToken(new Claim("idtyp", "user")));
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            response.Headers.TryGetValues("X-Authentication-Failure", out var failures)
                ? failures.Single()
                : "The bearer handler rejected the token without a validation failure."
        );
    }

    [Fact]
    public async Task EntraShapedClaims_RejectATenantMismatchAfterGenericBearerValidation()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var token = issuer.Issue(
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            "api",
            issuer.Key,
            issuer.KeyId,
            claims:
            [
                new Claim("tid", "cccccccc-cccc-cccc-cccc-cccccccccccc"),
                new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                new Claim("idtyp", "user"),
            ]
        );
        var response = await host.SendAsync("generic", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EntraShapedClaims_NormalizeApplicationRolesAfterGenericBearerValidation()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync(
            "generic",
            issuer.EntraToken(new Claim("roles", "Administrator"), new Claim("idtyp", "user"))
        );
        Assert.Equal("Administrator", response.Headers.GetValues("X-Roles").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_NormalizeDirectGroupsAfterGenericBearerValidation()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync(
            "generic",
            issuer.EntraToken(new Claim("groups", "group-a"), new Claim("idtyp", "user"))
        );
        Assert.Equal("group-a", response.Headers.GetValues("X-GroupIds").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_FailClosedForTheExactClaimNamesGroupOverageIndicator()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync(
            "generic",
            issuer.EntraToken(new Claim("_claim_names", "{\"groups\":\"source\"}"), new Claim("idtyp", "user"))
        );
        Assert.Equal("Indeterminate", response.Headers.GetValues("X-Groups").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_FailClosedForTheExactHasgroupsOverageIndicator()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync(
            "generic",
            issuer.EntraToken(new Claim("hasgroups", "true"), new Claim("idtyp", "user"))
        );
        Assert.Equal("Indeterminate", response.Headers.GetValues("X-Groups").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_TreatAbsentGroupsAsCompleteEmptyMembership()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync("generic", issuer.EntraToken(new Claim("idtyp", "user")));
        Assert.Equal("Complete", response.Headers.GetValues("X-Groups").Single());
        Assert.Empty(response.Headers.GetValues("X-GroupIds").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_NormalizeAUserToken()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync("generic", issuer.EntraToken(new Claim("idtyp", "user")));
        Assert.Equal("User", response.Headers.GetValues("X-Kind").Single());
    }

    [Fact]
    public async Task EntraShapedClaims_NormalizeAServicePrincipalToken()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(
            issuer,
            issuer.EntraIssuer(InProcessOidcIssuer.AllowlistedTenant),
            true
        );
        var response = await host.SendAsync("generic", issuer.EntraToken(new Claim("idtyp", "app")));
        Assert.Equal("ServicePrincipal", response.Headers.GetValues("X-Kind").Single());
    }

    [Fact]
    public async Task Router_EmitsOneFallbackChallengeForAMalformedBearer()
    {
        await using var issuer = await InProcessOidcIssuer.StartAsync();
        await using var host = await RegisteredBearerHost.StartAsync(issuer);
        var response = await host.SendAsync(null, "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(response.Headers.WwwAuthenticate);
    }
}
