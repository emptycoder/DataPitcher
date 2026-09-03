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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(scan.ScanId, "connection_failed", cancellationToken);
            return;
        }
        IConnectionProvider provider;
        try
        {
            provider = providers.Get(profile.ProviderId);
        }
        catch (UnsupportedConnectionProviderException)
        {
            await snapshots.FailAsync(scan.ScanId, "unsupported_provider", cancellationToken);
            return;
        }
        string resolved;
        try
        {
            resolved = await resolver.ResolveAsync(profile.SecretReference, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(scan.ScanId, "connection_failed", cancellationToken);
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await snapshots.FailAsync(scan.ScanId, "schema_scan_failed", cancellationToken);
        }
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
