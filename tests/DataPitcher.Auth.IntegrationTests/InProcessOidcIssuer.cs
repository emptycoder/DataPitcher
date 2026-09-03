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
    public string BaseAddress { get; private set; } = "";
    public string KeyId { get; } = "test-key";
    public RSA Key { get; } = RSA.Create(2048);

    public static async Task<InProcessOidcIssuer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var application = builder.Build();
        var issuer = new InProcessOidcIssuer(application);
        application.MapGet(
            "/.well-known/openid-configuration",
            () => Results.Json(issuer.Discovery(issuer.BaseAddress))
        );
        application.MapGet(
            "/{tenant}/v2.0/.well-known/openid-configuration",
            (string tenant) =>
                Results.Json(
                    issuer.Discovery(
                        tenant == "organizations" ? issuer.BaseAddress + "/{tenantid}/v2.0" : issuer.EntraIssuer(tenant)
                    )
                )
        );
        application.MapGet("/keys", () => Results.Json(issuer.Jwks()));
        await application.StartAsync();
        issuer.BaseAddress = application.Urls.Single();
        return issuer;
    }

    public static RSA NewKey() => RSA.Create(2048);

    public string EntraIssuer(string tenant) => BaseAddress + "/" + tenant + "/v2.0";

    public string EntraToken(params Claim[] claims) =>
        Issue(
            EntraIssuer(AllowlistedTenant),
            "api",
            Key,
            KeyId,
            claims:
            [
                new Claim("tid", AllowlistedTenant),
                new Claim("oid", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                new Claim("scp", "api.read"),
                .. claims,
            ]
        );

    public string Issue(
        string issuer,
        string audience,
        RSA signingKey,
        string keyId,
        DateTime? expires = null,
        DateTime? notBefore = null,
        params Claim[] claims
    )
    {
        var startsAt = notBefore ?? (expires is null ? DateTime.UtcNow.AddMinutes(-1) : expires.Value.AddMinutes(-1));
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(5);
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(
                issuer,
                audience,
                [new Claim("sub", "subject"), .. claims],
                startsAt,
                expiresAt,
                new SigningCredentials(new RsaSecurityKey(signingKey) { KeyId = keyId }, SecurityAlgorithms.RsaSha256)
            )
        );
    }

    public ValueTask DisposeAsync()
    {
        Key.Dispose();
        return app.DisposeAsync();
    }

    private object Discovery(string issuer) =>
        new
        {
            issuer,
            jwks_uri = BaseAddress + "/keys",
            authorization_endpoint = BaseAddress + "/authorize",
            token_endpoint = BaseAddress + "/token",
        };

    private object Jwks()
    {
        var parameters = Key.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        };
    }
}

internal sealed class RegisteredBearerHost(WebApplication app) : IAsyncDisposable
{
    private readonly TestServer server = app.GetTestServer();

    public static async Task<RegisteredBearerHost> StartAsync(
        InProcessOidcIssuer issuer,
        string? validationIssuer = null,
        bool useEntraClaims = false
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["Generic:SchemeName"] = "generic",
            ["Generic:ProviderInstance"] = "generic-test",
            ["Generic:Issuer"] = validationIssuer ?? issuer.BaseAddress,
            ["Generic:Audience"] = "api",
            ["Generic:PrincipalKind"] = "User",
            ["Generic:RequiredScopes:0"] = "api.read",
            ["Entra:SchemeName"] = "entra",
            ["Entra:ProviderInstance"] = "entra-test",
            ["Entra:Instance"] = issuer.BaseAddress + "/",
            ["Entra:TenantId"] = "organizations",
            ["Entra:ClientId"] = "client",
            ["Entra:Audience"] = "api",
            ["Entra:AllowedTenantIds:0"] = InProcessOidcIssuer.AllowlistedTenant,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var generic = new GenericOpenIdConnectProviderRegistration(configuration.GetSection("Generic"));
        var entra = new EntraProviderRegistration(configuration.GetSection("Entra"));
        builder.Services.AddDataPitcherAuthentication(
            "DataPitcher.Router",
            "generic",
            new IAuthProviderRegistration[] { generic }
        );
        entra.Register(builder.Services.AddAuthentication());
        builder.Services.Configure<JwtBearerOptions>(
            "generic",
            options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                options.Events.OnAuthenticationFailed = context =>
                {
                    context.HttpContext.Items["AuthenticationFailure"] = context.Exception.Message;
                    return Task.CompletedTask;
                };
            }
        );
        builder.Services.Configure<JwtBearerOptions>(
            "entra",
            options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                options.Events.OnAuthenticationFailed = context =>
                {
                    context.HttpContext.Items["AuthenticationFailure"] = context.Exception.Message;
                    return Task.CompletedTask;
                };
            }
        );
        if (useEntraClaims)
        {
            builder.Services.Configure<JwtBearerOptions>(
                "generic",
                options =>
                {
                    var prior = options.Events.OnTokenValidated;
                    options.Events.OnTokenValidated = async context =>
                    {
                        if (prior is not null)
                            await prior(context);
                        var entraTokenValidated = context
                            .HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>()
                            .Get("entra")
                            .Events.OnTokenValidated;
                        if (entraTokenValidated is not null)
                            await entraTokenValidated(context);
                    };
                }
            );
        }
        var application = builder.Build();
        application.Run(context => ProbeAsync(context, issuer, validationIssuer ?? issuer.BaseAddress, useEntraClaims));
        await application.StartAsync();
        return new RegisteredBearerHost(application);
    }

    public async Task<HttpResponseMessage> SendAsync(string? scheme, string token)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, scheme is null ? "/" : "/?scheme=" + scheme);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public ValueTask DisposeAsync()
    {
        return app.DisposeAsync();
    }

    private static async Task ProbeAsync(
        Microsoft.AspNetCore.Http.HttpContext context,
        InProcessOidcIssuer issuer,
        string validatedIssuer,
        bool useEntraClaims
    )
    {
        var scheme = context.Request.Query["scheme"].FirstOrDefault();
        var result = await context.AuthenticateAsync(scheme);
        if (!result.Succeeded)
        {
            if (context.Items.TryGetValue("AuthenticationFailure", out var failure))
                context.Response.Headers["X-Authentication-Failure"] = failure?.ToString();
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.ChallengeAsync(scheme);
            return;
        }
        var selected = useEntraClaims ? "entra" : scheme ?? result.Ticket!.AuthenticationScheme;
        var normalizer = context.RequestServices.GetRequiredKeyedService<IExternalPrincipalNormalizer>(selected);
        var normalized = normalizer.Normalize(result.Principal!, validatedIssuer);
        context.Response.Headers["X-Roles"] = string.Join(",", normalized.RoleValues);
        context.Response.Headers["X-Groups"] = normalized.GroupResolution.State.ToString();
        context.Response.Headers["X-GroupIds"] = string.Join(",", normalized.GroupResolution.ImmutableGroupIds);
        context.Response.Headers["X-Kind"] = normalized.Principal.AuthorizationKey.PrincipalKind.ToString();
        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
    }
}
