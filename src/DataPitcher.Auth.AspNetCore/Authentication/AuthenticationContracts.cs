using System.Security.Claims;
using DataPitcher.Auth.Abstractions.Identity;
using Microsoft.AspNetCore.Authentication;

namespace DataPitcher.Auth.AspNetCore.Authentication;

public interface IAuthProviderRegistration
{
    string SchemeName { get; }
    IReadOnlyCollection<IssuerRoute> Routes { get; }
    void Register(AuthenticationBuilder builder);
}

public interface IExternalPrincipalNormalizer
{
    AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer);
}

public sealed record AuthenticatedProviderPrincipal(
    NormalizedPrincipal Principal,
    IReadOnlyCollection<string> RoleValues,
    GroupResolutionResult GroupResolution,
    IReadOnlyCollection<string> ScopeValues
);

public enum IssuerRouteKind
{
    Exact,
    EntraV2,
}

public sealed class IssuerRoute
{
    private IssuerRoute(string schemeName, string authority, IssuerRouteKind kind)
    {
        SchemeName = schemeName;
        Authority = authority.TrimEnd('/');
        Kind = kind;
    }

    public string SchemeName { get; }
    public string Authority { get; }
    public IssuerRouteKind Kind { get; }

    public static IssuerRoute Exact(string schemeName, string issuer) => new(schemeName, issuer, IssuerRouteKind.Exact);

    public static IssuerRoute EntraV2(string schemeName, string instance) =>
        new(schemeName, instance, IssuerRouteKind.EntraV2);

    public bool Matches(string issuer) =>
        Kind == IssuerRouteKind.Exact
            ? StringComparer.Ordinal.Equals(Authority, issuer.TrimEnd('/'))
            : Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
                && StringComparer.OrdinalIgnoreCase.Equals(Authority, uri.GetLeftPart(UriPartial.Authority))
                && uri.Segments.Length == 3
                && StringComparer.Ordinal.Equals(uri.Segments[2].TrimEnd('/'), "v2.0");

    public bool Overlaps(IssuerRoute other) =>
        Kind == IssuerRouteKind.Exact && other.Kind == IssuerRouteKind.Exact ? Matches(other.Authority)
        : Kind == IssuerRouteKind.Exact ? other.Matches(Authority)
        : other.Kind == IssuerRouteKind.Exact ? Matches(other.Authority)
        : StringComparer.OrdinalIgnoreCase.Equals(Authority, other.Authority);
}
