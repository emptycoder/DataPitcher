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

    public ConnectionNotHealthyException(string message)
        : base(message) { }
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
        var source = await CheckDetailedAsync(
            run.SourceConnectionId,
            run.TransferMode,
            ConnectionRole.Source,
            cancellationToken
        );
        if (!IsUsable(source.Summary.Health))
            throw new ConnectionNotHealthyException(Explain(source, ConnectionRole.Source, run.TransferMode));
        var target = await CheckDetailedAsync(
            run.TargetConnectionId,
            run.TransferMode,
            ConnectionRole.Target,
            cancellationToken
        );
        if (!IsUsable(target.Summary.Health))
            throw new ConnectionNotHealthyException(Explain(target, ConnectionRole.Target, run.TransferMode));
    }

    /// <summary>Names the connection, the role it failed in, what is missing and what the probe saw.</summary>
    private static string Explain(CheckResult result, ConnectionRole role, TransferMode mode)
    {
        var missing = result.Assessment.MissingRequired.Select(capability => capability.ToString()).Order().ToArray();
        var reason =
            result.Evidence.CleanupFailureCode is "connection_failed" ? "could not be reached"
            : result.Evidence.CleanupFailureCode is not null
                ? "left a staging object behind (" + result.Evidence.CleanupFailureCode + ")"
            : missing.Length > 0 ? "is missing " + string.Join(", ", missing)
            : "is not healthy";
        var notes = result.Evidence.Notes.Count == 0 ? "" : " " + string.Join(" ", result.Evidence.Notes);
        return $"{role} connection '{result.DisplayName}' {reason} for a {mode} transfer.{notes}";
    }

    public static bool IsUsable(ConnectionHealthState health) =>
        health is ConnectionHealthState.Healthy or ConnectionHealthState.Degraded;

    private async Task<ConnectionProfileSummary> CheckAsync(
        Guid connectionId,
        TransferMode mode,
        ConnectionRole role,
        CancellationToken cancellationToken
    ) => (await CheckDetailedAsync(connectionId, mode, role, cancellationToken)).Summary;

    private sealed record CheckResult(
        string DisplayName,
        ConnectionProfileSummary Summary,
        ConnectionAssessment Assessment,
        ConnectionProbeEvidence Evidence
    );

    private async Task<CheckResult> CheckDetailedAsync(
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            evidence = new ConnectionProbeEvidence(
                "",
                "",
                Array.Empty<ConnectionCapability>(),
                "connection_failed",
                [exception.GetBaseException().Message]
            );
        }
        var assessment = ConnectionHealthClassifier.Classify(ConnectionRequirements.For(mode, role), evidence);
        var summary = await profiles.SaveAssessmentAsync(connectionId, mode, role, assessment, cancellationToken);
        return new CheckResult(profile.DisplayName, summary, assessment, evidence);
    }
}
