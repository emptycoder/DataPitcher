using System.Security.Claims;
using System.Text;
using DataPitcher.Auth.Abstractions.Identity;
using DataPitcher.Auth.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace DataPitcher.Auth.Development;

public sealed class DevelopmentProviderOptions
{
    public string SchemeName { get; init; } = "";
    public string ProviderInstance { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string SigningKey { get; init; } = "";
}

public sealed class DevelopmentProviderRegistration : IAuthProviderRegistration
{
    private readonly DevelopmentProviderOptions options;

    public DevelopmentProviderRegistration(IHostEnvironment environment, DevelopmentProviderOptions options)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
            throw new InvalidOperationException("Development authentication is allowed only in Development or Test.");
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException(
                "Development signing key is required. Set Authentication__Development__SigningKey to a local value of at least 32 bytes."
            );
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
            throw new ArgumentException("Development signing key must be at least 32 bytes.", nameof(options));
    }

    public string SchemeName => options.SchemeName;
    public IReadOnlyCollection<IssuerRoute> Routes => new[] { IssuerRoute.Exact(SchemeName, options.Issuer) };

    public void Register(AuthenticationBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IExternalPrincipalNormalizer>(
            SchemeName,
            new DevelopmentPrincipalNormalizer(options)
        );
        builder.AddJwtBearer(
            SchemeName,
            bearer =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,
                };
            }
        );
    }
}

internal sealed class DevelopmentPrincipalNormalizer(DevelopmentProviderOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer)
    {
        var subject =
            principal.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("Validated development token has no sub claim.");
        var key = new ExternalPrincipalKey(
            options.ProviderInstance,
            validatedIssuer,
            null,
            PrincipalKind.User,
            subject
        );
        return new AuthenticatedProviderPrincipal(
            new NormalizedPrincipal(key, new(null, null, null, null)),
            principal.FindAll("roles").Select(claim => claim.Value).ToArray(),
            GroupResolutionResult.Complete(principal.FindAll("groups").Select(claim => claim.Value)),
            Array.Empty<string>()
        );
    }
}
