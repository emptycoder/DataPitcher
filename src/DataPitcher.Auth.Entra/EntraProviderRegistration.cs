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
    public string SchemeName { get; init; } = "";
    public string ProviderInstance { get; init; } = "";
    public string Instance { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string? Audience { get; init; }
    public string[] AllowedTenantIds { get; init; } = Array.Empty<string>();
}

public sealed class EntraProviderRegistration : IAuthProviderRegistration
{
    private readonly EntraProviderOptions options;
    private readonly IConfigurationSection configuration;

    public EntraProviderRegistration(IConfigurationSection configuration)
    {
        this.configuration = configuration;
        options =
            configuration.Get<EntraProviderOptions>()
            ?? throw new ArgumentException("Entra configuration is required.", nameof(configuration));
        Validate(options);
    }

    public string SchemeName => options.SchemeName;
    public IReadOnlyCollection<IssuerRoute> Routes => new[] { IssuerRoute.EntraV2(SchemeName, options.Instance) };

    public void Register(AuthenticationBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IExternalPrincipalNormalizer>(
            SchemeName,
            new EntraPrincipalNormalizer(options)
        );
        builder.AddMicrosoftIdentityWebApi(configuration, SchemeName);
        builder.Services.PostConfigure<JwtBearerOptions>(
            SchemeName,
            bearer =>
            {
                bearer.MapInboundClaims = false;
                var prior = bearer.Events.OnTokenValidated;
                bearer.Events.OnTokenValidated = async context =>
                {
                    if (prior is not null)
                        await prior(context);
                    if (
                        IsMultiTenant
                        && (
                            !Guid.TryParse(context.Principal?.FindFirst("tid")?.Value, out var tenant)
                            || !options.AllowedTenantIds.Contains(tenant.ToString(), StringComparer.OrdinalIgnoreCase)
                        )
                    )
                        context.Fail("Tenant is not allowlisted.");
                };
            }
        );
    }

    private bool IsMultiTenant => StringComparer.Ordinal.Equals(options.TenantId, "organizations");

    private static void Validate(EntraProviderOptions options)
    {
        if (
            string.IsNullOrWhiteSpace(options.SchemeName)
            || string.IsNullOrWhiteSpace(options.ProviderInstance)
            || !Uri.TryCreate(options.Instance, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(options.ClientId)
        )
            throw new ArgumentException(
                "Entra scheme, provider instance, absolute instance, and client ID are required.",
                nameof(options)
            );
        if (StringComparer.Ordinal.Equals(options.TenantId, "organizations"))
        {
            if (
                options.AllowedTenantIds.Length == 0
                || options.AllowedTenantIds.Any(value => !Guid.TryParse(value, out _))
            )
                throw new ArgumentException(
                    "Multi-tenant Entra configuration requires GUID allowlisted tenants.",
                    nameof(options)
                );
        }
        else if (!Guid.TryParse(options.TenantId, out _))
            throw new ArgumentException(
                "Single-tenant Entra configuration requires an explicit tenant GUID.",
                nameof(options)
            );
    }
}
