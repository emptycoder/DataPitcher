using DataPitcher.Application.Connections;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Identity;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Application.Worker;

/// <summary>A database provider's implementation of the transfer read and target sessions the worker drives.</summary>
/// <summary>
/// Routes each run to the provider of the connection it reads from or writes to, so the job worker stays provider
/// agnostic. Sealing already guarantees that source and target share a provider.
/// </summary>
public sealed class ProviderRunSessionRouter(
    IConnectionProfileRepository profiles,
    IEnumerable<IRunSessionProvider> providers
) : ITransferReadSessionFactory, ITargetRunSessionFactory
{
    private readonly IReadOnlyDictionary<string, IRunSessionProvider> providers = providers.ToDictionary(
        provider => provider.ProviderId,
        StringComparer.Ordinal
    );

    public async Task<ITransferReadSession> OpenKeysetAsync(
        TransferRun run,
        StableKey? startAfter,
        CancellationToken cancellationToken,
        TableAddress? table = null,
        TransferPhase phase = TransferPhase.Rows
    ) =>
        await (await ProviderForAsync(run.SourceConnectionId, cancellationToken)).OpenKeysetAsync(
            run,
            startAfter,
            cancellationToken,
            table,
            phase
        );

    public async Task<ITargetRunSession> OpenAsync(TransferRun run, CancellationToken cancellationToken) =>
        await (await ProviderForAsync(run.TargetConnectionId, cancellationToken)).OpenAsync(run, cancellationToken);

    private async Task<IRunSessionProvider> ProviderForAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetProfileAsync(connectionId, cancellationToken);
        return providers.TryGetValue(profile.ProviderId, out var provider)
            ? provider
            : throw new NotSupportedException(
                $"Transfer execution is not available for the '{profile.ProviderId}' provider."
            );
    }
}
