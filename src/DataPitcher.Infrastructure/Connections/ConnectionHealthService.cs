using DataPitcher.Core.Connections;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Worker;

namespace DataPitcher.Infrastructure.Connections;

public sealed class ConnectionNotHealthyException : InvalidOperationException
{
    public ConnectionNotHealthyException() : base("Connection health revalidation failed.") { }
}

public sealed class ConnectionHealthService(ConnectionProfileStore profiles, ISecretReferenceResolver resolver, IConnectionProviderRegistry providers) : ITransferConnectionRevalidator
{
    public Task<ConnectionProfileSummary> TestAsync(Guid connectionId, TransferMode mode, ConnectionRole role, CancellationToken cancellationToken) => CheckAsync(connectionId, mode, role, cancellationToken);

    public Task<ConnectionProfileSummary> RecheckAsync(Guid connectionId, TransferMode mode, ConnectionRole role, CancellationToken cancellationToken) => CheckAsync(connectionId, mode, role, cancellationToken);

    public async Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken)
    {
        if ((await RecheckAsync(run.SourceConnectionId, run.TransferMode, ConnectionRole.Source, cancellationToken)).Health is not ConnectionHealthState.Healthy)
            throw new ConnectionNotHealthyException();
        if ((await RecheckAsync(run.TargetConnectionId, run.TransferMode, ConnectionRole.Target, cancellationToken)).Health is not ConnectionHealthState.Healthy)
            throw new ConnectionNotHealthyException();
    }

    private async Task<ConnectionProfileSummary> CheckAsync(Guid connectionId, TransferMode mode, ConnectionRole role, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetProfileAsync(connectionId, cancellationToken);
        await profiles.MarkCheckingAsync(connectionId, mode, role, cancellationToken);
        ConnectionProbeEvidence evidence;
        try
        {
            var provider = providers.Get(profile.ProviderId);
            var resolved = await resolver.ResolveAsync(profile.SecretReference, cancellationToken);
            evidence = await provider.CapabilityDetector.ProbeAsync(new ConnectionProbeRequest(profile, role, mode, resolved), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            evidence = new ConnectionProbeEvidence("", "", Array.Empty<ConnectionCapability>(), "connection_failed");
        }
        return await profiles.SaveAssessmentAsync(connectionId, mode, role, ConnectionHealthClassifier.Classify(ConnectionRequirements.For(mode, role), evidence), cancellationToken);
    }
}
