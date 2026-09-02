using System.Security.Claims;
using System.Text.Json;
using DataPitcher.Auth.AspNetCore.Authentication;
using DataPitcher.Auth.Abstractions.Identity;

namespace DataPitcher.Auth.Entra;

public sealed class EntraPrincipalNormalizer(EntraProviderOptions options) : IExternalPrincipalNormalizer
{
    public AuthenticatedProviderPrincipal Normalize(ClaimsPrincipal principal, string validatedIssuer)
    {
        var tenant = RequiredGuid(principal, "tid"); var objectId = RequiredGuid(principal, "oid");
        var kind = principal.FindFirst("idtyp")?.Value switch { null => PrincipalKind.User, "user" => PrincipalKind.User, "app" => PrincipalKind.ServicePrincipal, _ => throw new InvalidOperationException("Validated Entra token has an unsupported idtyp claim.") };
        var key = new ExternalPrincipalKey(options.ProviderInstance, validatedIssuer, tenant, kind, objectId);
        var presentation = new PrincipalPresentation(principal.FindFirst("name")?.Value, principal.FindFirst("email")?.Value, principal.FindFirst("preferred_username")?.Value, principal.FindFirst("upn")?.Value);
        return new AuthenticatedProviderPrincipal(new NormalizedPrincipal(key, presentation), principal.FindAll("roles").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(), Groups(principal), principal.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
    private static string RequiredGuid(ClaimsPrincipal principal, string type) => Guid.TryParse(principal.FindFirst(type)?.Value, out var value) ? value.ToString() : throw new InvalidOperationException($"Validated Entra token has no GUID {type} claim.");
    private static GroupResolutionResult Groups(ClaimsPrincipal principal)
    {
        var names = principal.FindFirst("_claim_names")?.Value;
        var overageByNames = names is not null && IsGroupOverage(names);
        var overageByFlag = StringComparer.OrdinalIgnoreCase.Equals(principal.FindFirst("hasgroups")?.Value, "true");
        return overageByNames || overageByFlag ? GroupResolutionResult.Indeterminate() : GroupResolutionResult.Complete(principal.FindAll("groups").Select(claim => claim.Value));
    }
    private static bool IsGroupOverage(string names) { try { using var document = JsonDocument.Parse(names); return document.RootElement.TryGetProperty("groups", out _); } catch (JsonException) { return true; } }
}
