using DataPitcher.Core.Plans;

namespace DataPitcher.Core.Connections;

public sealed record ConnectionProfileDraft(
    string DisplayName,
    string ProviderId,
    SecretReference SecretReference,
    string BusinessSchema,
    string StagingSchema
);

public interface IConnectionProfileRepository
{
    Task<ConnectionProfile> CreateAsync(
        ConnectionProfileDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken
    );

    Task<ConnectionProfileSummary> GetSummaryAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionProfileSummary>> ListSummariesAsync(CancellationToken cancellationToken);

    Task<ConnectionProfile> UpdateAsync(
        Guid connectionId,
        ConnectionProfileDraft draft,
        string ifMatch,
        CancellationToken cancellationToken
    );

    Task DeleteAsync(Guid connectionId, string ifMatch, CancellationToken cancellationToken);

    Task<ConnectionProfile> GetProfileAsync(Guid connectionId, CancellationToken cancellationToken);

    Task MarkCheckingAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    );

    Task<ConnectionProfileSummary> SaveAssessmentAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        ConnectionAssessment assessment,
        CancellationToken cancellationToken
    );
}

public interface ISecretReferenceResolver
{
    Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}

public interface ISecretWriter
{
    Task<SecretReference> StoreAsync(Guid credentialId, string secret, CancellationToken cancellationToken);
    Task RemoveAsync(SecretReference reference, CancellationToken cancellationToken);
}
