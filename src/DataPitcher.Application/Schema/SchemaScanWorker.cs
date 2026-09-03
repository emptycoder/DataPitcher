using DataPitcher.Application.Connections;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Application.Schema;

public sealed class SchemaScanWorker(
    ISchemaSnapshotRepository snapshots,
    IConnectionProfileRepository profiles,
    ISecretReferenceResolver resolver,
    IConnectionProviderRegistry providers
) : BackgroundService
{
    public async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        var scan = await snapshots.ClaimNextAsync(cancellationToken);
        if (scan is null)
            return;
        ConnectionProfile profile;
        try
        {
            profile = await profiles.GetProfileAsync(scan.ConnectionId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(scan.ScanId, "connection_failed", Detail(exception, null), cancellationToken);
            return;
        }
        IConnectionProvider provider;
        try
        {
            provider = providers.Get(profile.ProviderId);
        }
        catch (UnsupportedConnectionProviderException exception)
        {
            await snapshots.FailAsync(scan.ScanId, "unsupported_provider", Detail(exception, null), cancellationToken);
            return;
        }
        string resolved;
        try
        {
            resolved = await resolver.ResolveAsync(profile.SecretReference, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(scan.ScanId, "connection_failed", Detail(exception, null), cancellationToken);
            return;
        }
        try
        {
            await snapshots.CompleteAsync(
                scan,
                await provider.SchemaIntrospector.ReadAsync(profile, resolved, cancellationToken),
                cancellationToken
            );
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(
                scan.ScanId,
                "schema_scan_failed",
                Detail(exception, resolved),
                cancellationToken
            );
        }
    }

    /// <summary>The driver's own explanation, with the connection string and its password removed.</summary>
    internal static string Detail(Exception exception, string? resolvedConnectionString)
    {
        var message = exception.GetBaseException().Message;
        if (!string.IsNullOrEmpty(resolvedConnectionString))
        {
            message = message.Replace(resolvedConnectionString, "[connection string]", StringComparison.Ordinal);
            if (ConnectionStringSecrets.TryExtractPassword(resolvedConnectionString) is { } password)
                message = message.Replace(password, "[password]", StringComparison.Ordinal);
        }
        return message.Length > 2000 ? message[..2000] : message;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessNextAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
