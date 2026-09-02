using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Entra;
using DataPitcher.Auth.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using DataPitcher.Auth.Development;
#endif

namespace DataPitcher.Auth.Hosting;

public static class DataPitcherAuthenticationHostingExtensions
{
    public static IServiceCollection AddDataPitcherAuthenticationProviders(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var providers = new List<IAuthProviderRegistration>();
        if (configuration.GetValue<bool>("Authentication:Entra:Enabled")) providers.Add(new EntraProviderRegistration(configuration.GetSection("Authentication:Entra")));
        if (configuration.GetValue<bool>("Authentication:Generic:Enabled")) providers.Add(new GenericOpenIdConnectProviderRegistration(configuration.GetSection("Authentication:Generic")));
#if DEBUG
        if (configuration.GetValue<bool>("Authentication:Development:Enabled")) providers.Add(new DevelopmentProviderRegistration(environment, configuration.GetSection("Authentication:Development").Get<DevelopmentProviderOptions>() ?? throw new InvalidOperationException("Development authentication configuration is required.")));
#else
        if (configuration.GetValue<bool>("Authentication:Development:Enabled")) throw new InvalidOperationException("Development authentication cannot be enabled in a Release artifact.");
#endif
        if (providers.Count > 0) return services.AddDataPitcherAuthentication("DataPitcher.Router", configuration["Authentication:FallbackScheme"] ?? providers[0].SchemeName, providers.ToArray());
        throw new InvalidOperationException("At least one authentication provider must be enabled.");
    }
}
