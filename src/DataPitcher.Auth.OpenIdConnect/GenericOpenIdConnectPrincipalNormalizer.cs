using System.Security.Claims;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;

namespace DataPitcher.Auth.OpenIdConnect;

public sealed class GenericOpenIdConnectPrincipalNormalizer(GenericOpenIdConnectOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer)
    {
        var subject = principal.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Validated generic OIDC token has no sub claim.");
        var key = new ExternalPrincipalKey(options.ProviderInstance, validatedIssuer, null, options.PrincipalKind, subject);
        var presentation = new PrincipalPresentation(principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value, principal.FindFirst("preferred_username")?.Value, null);
        var groups = options.GroupClaimType is null ? GroupResolutionResult.NotApplicable() : GroupResolutionResult.Complete(principal.FindAll(options.GroupClaimType).Select(claim => claim.Value));
        return new AuthenticatedProviderPrincipal(new NormalizedPrincipal(key, presentation), principal.FindAll("roles").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(), groups, principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
