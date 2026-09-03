using DataPitcher.Application.Worker;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Connections;

public sealed class ConnectionNotHealthyException : InvalidOperationException
{
    public ConnectionNotHealthyException()
        : base("Connection health revalidation failed.") { }
}

public sealed class ConnectionHealthService(
    IConnectionProfileRepository profiles,
    ISecretReferenceResolver resolver,
    IConnectionProviderRegistry providers
) : ITransferConnectionRevalidator
{
    public Task<ConnectionProfileSummary> TestAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    ) => CheckAsync(connectionId, mode, role, cancellationToken);

    public Task<ConnectionProfileSummary> RecheckAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    ) => CheckAsync(connectionId, mode, role, cancellationToken);

    /// <summary>
    /// Re-probes both connections before a transfer runs. Degraded connections (every required capability present,
    /// only optional ones such as snapshot isolation missing) are allowed to proceed.
    /// </summary>
    public async Task RevalidateAsync(TransferRun run, CancellationToken cancellationToken)
    {
        if (
            !IsUsable(
                (
                    await RecheckAsync(
                        run.SourceConnectionId,
                        run.TransferMode,
                        ConnectionRole.Source,
                        cancellationToken
                    )
                ).Health
            )
        )
            throw new ConnectionNotHealthyException();
        if (
            !IsUsable(
                (
                    await RecheckAsync(
                        run.TargetConnectionId,
                        run.TransferMode,
                        ConnectionRole.Target,
                        cancellationToken
                    )
                ).Health
            )
        )
            throw new ConnectionNotHealthyException();
    }

    public static bool IsUsable(ConnectionHealthState health) =>
        health is ConnectionHealthState.Healthy or ConnectionHealthState.Degraded;

    private async Task<ConnectionProfileSummary> CheckAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    )
    {
        var profile = await profiles.GetProfileAsync(connectionId, cancellationToken);
        await profiles.MarkCheckingAsync(connectionId, mode, role, cancellationToken);
        ConnectionProbeEvidence evidence;
        try
        {
            var provider = providers.Get(profile.ProviderId);
            var resolved = await resolver.ResolveAsync(profile.SecretReference, cancellationToken);
            evidence = await provider.CapabilityDetector.ProbeAsync(
                new ConnectionProbeRequest(profile, role, mode, resolved),
                cancellationToken
            );
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            evidence = new ConnectionProbeEvidence("", "", Array.Empty<ConnectionCapability>(), "connection_failed");
        }
        return await profiles.SaveAssessmentAsync(
            connectionId,
            mode,
            role,
            ConnectionHealthClassifier.Classify(ConnectionRequirements.For(mode, role), evidence),
            cancellationToken
        );
    }
}
