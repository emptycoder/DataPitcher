namespace DataPitcher.Auth.Abstractions.Identity;

public sealed record PrincipalPresentation(string? DisplayName, string? Email, string? Username, string? PrincipalName);

public sealed record NormalizedPrincipal(ExternalPrincipalKey AuthorizationKey, PrincipalPresentation Presentation);
