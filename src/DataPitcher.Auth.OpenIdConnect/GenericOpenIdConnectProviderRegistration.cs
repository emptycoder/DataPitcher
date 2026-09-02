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
