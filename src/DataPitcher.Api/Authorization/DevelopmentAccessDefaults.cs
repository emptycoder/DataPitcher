using System.Security.Claims;

namespace DataPitcher.Api.Authorization;

/// <summary>
/// Permissive resource-grant reader for local development and test hosting only. There is no persisted
/// per-resource access-control list yet, so every authenticated principal is granted access to every resource.
/// </summary>
public sealed class DevelopmentResourceAccessGrantReader : IResourceAccessGrantReader
{
    public Task<bool> IsGrantedAsync(ClaimsPrincipal principal, ApiResource resource, CancellationToken cancellationToken) =>
        Task.FromResult(true);
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
