using System.Security.Claims;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataPitcher.Api.Authorization;

/// <summary>
/// Permissive resource-grant reader for local development and test hosting only. There is no persisted
/// per-resource access-control list yet, so every authenticated principal is granted access to every resource.
/// </summary>
public sealed class DevelopmentResourceAccessGrantReader : IResourceAccessGrantReader
{
    public Task<bool> IsGrantedAsync(
        ClaimsPrincipal principal,
        ApiResource resource,
        CancellationToken cancellationToken
    ) => Task.FromResult(true);
}

/// <summary>
/// Registers <see cref="DevelopmentResourceAccessGrantReader"/> only in a Debug build with
/// "Authorization:DevelopmentGrants:Enabled" set, mirroring the guard on the development authentication provider:
/// startup fails rather than a permissive default silently activating in a Release artifact.
/// </summary>
public static class DevelopmentAccessDefaultsRegistration
{
    public static IServiceCollection AddDevelopmentResourceAccessGrantReader(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
#if DEBUG
        if (configuration.GetValue<bool>("Authorization:DevelopmentGrants:Enabled"))
            services.AddSingleton<IResourceAccessGrantReader, DevelopmentResourceAccessGrantReader>();
#else
        if (configuration.GetValue<bool>("Authorization:DevelopmentGrants:Enabled"))
            throw new InvalidOperationException(
                "Development resource-access grants cannot be enabled in a Release artifact."
            );
#endif
        return services;
    }
}

/// <summary>
/// Reads the standard JWT <c>exp</c> claim (seconds since the Unix epoch) left on the validated principal by the
/// registered bearer scheme. Falls back to a short expiry when the claim is absent so a stream cannot be held open
/// indefinitely against a principal whose expiry could not be determined.
/// </summary>
public sealed class DevelopmentValidatedAccessTokenLifetime : IValidatedAccessTokenLifetime
{
    public DateTimeOffset GetExpiryUtc(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst("exp")?.Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow.AddMinutes(5);
}
