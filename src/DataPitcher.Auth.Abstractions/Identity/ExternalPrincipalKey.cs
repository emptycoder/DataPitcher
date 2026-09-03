namespace DataPitcher.Auth.Abstractions.Identity;

public enum PrincipalKind
{
    User,
    ServicePrincipal,
}

public sealed record ExternalPrincipalKey
{
    public ExternalPrincipalKey(
        string providerInstance,
        string validatedIssuer,
        string? tenantId,
        PrincipalKind principalKind,
        string immutableSubject
    )
    {
        if (string.IsNullOrWhiteSpace(providerInstance) || string.IsNullOrWhiteSpace(immutableSubject))
            throw new ArgumentException("Provider instance and immutable subject are required.");
        if (!Uri.TryCreate(validatedIssuer, UriKind.Absolute, out _))
            throw new ArgumentException("Validated issuer must be an absolute URI.", nameof(validatedIssuer));
        if (tenantId is not null && string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant must be null or non-empty.", nameof(tenantId));
        ProviderInstance = providerInstance;
        ValidatedIssuer = validatedIssuer;
        TenantId = tenantId;
        PrincipalKind = principalKind;
        ImmutableSubject = immutableSubject;
    }

    public string ProviderInstance { get; }
    public string ValidatedIssuer { get; }
    public string? TenantId { get; }
    public PrincipalKind PrincipalKind { get; }
    public string ImmutableSubject { get; }
}
