using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DataPitcher.Auth.AspNetCore.Authentication;

public static class IssuerSchemeRouter
{
    public static string SelectScheme(
        HttpContext context,
        IReadOnlyCollection<IssuerRoute> routes,
        string fallbackScheme
    )
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return fallbackScheme;
        var token = header["Bearer ".Length..];
        var reader = new JwtSecurityTokenHandler();
        if (!reader.CanReadToken(token))
            return fallbackScheme;
        string issuer;
        try
        {
            issuer = reader.ReadJwtToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            return fallbackScheme;
        }
        return routes.SingleOrDefault(route => route.Matches(issuer))?.SchemeName ?? fallbackScheme;
    }
}

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddDataPitcherAuthentication(
        this IServiceCollection services,
        string policySchemeName,
        string fallbackSchemeName,
        IReadOnlyCollection<IAuthProviderRegistration> registrations
    )
    {
        var schemes = registrations.Select(registration => registration.SchemeName).ToArray();
        if (
            schemes.Length == 0
            || schemes.Distinct(StringComparer.Ordinal).Count() != schemes.Length
            || !schemes.Contains(fallbackSchemeName, StringComparer.Ordinal)
        )
            throw new InvalidOperationException(
                "Authentication schemes must be non-empty, unique, and include the fallback."
            );
        var routes = registrations.SelectMany(registration => registration.Routes).ToArray();
        foreach (var pair in routes.SelectMany((left, index) => routes.Skip(index + 1).Select(right => (left, right))))
            if (pair.left.Overlaps(pair.right))
                throw new InvalidOperationException(
                    $"Authentication issuer routes overlap: {pair.left.SchemeName} and {pair.right.SchemeName}."
                );
        var builder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = policySchemeName;
            options.DefaultChallengeScheme = policySchemeName;
        });
        foreach (var registration in registrations)
            registration.Register(builder);
        builder.AddPolicyScheme(
            policySchemeName,
            policySchemeName,
            options =>
                options.ForwardDefaultSelector = context =>
                    IssuerSchemeRouter.SelectScheme(context, routes, fallbackSchemeName)
        );
        return services;
    }
}
