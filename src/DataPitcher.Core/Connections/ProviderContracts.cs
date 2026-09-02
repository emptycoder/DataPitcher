using System.Collections.Frozen;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;

namespace DataPitcher.Core.Connections;

public sealed record ConnectionProbeRequest(
    ConnectionProfile Profile, ConnectionRole Role, TransferMode Mode, string ResolvedConnectionString);

public interface ICapabilityDetector
{
    Task<ConnectionProbeEvidence> ProbeAsync(ConnectionProbeRequest request, CancellationToken cancellationToken);
}

public interface ISchemaIntrospector
{
    Task<SchemaSnapshotContent> ReadAsync(ConnectionProfile profile, string resolvedConnectionString, CancellationToken cancellationToken);
}

public interface IConnectionProvider
{
    string ProviderId { get; }
    ICapabilityDetector CapabilityDetector { get; }
    ISchemaIntrospector SchemaIntrospector { get; }
}

public interface IConnectionProviderRegistry
{
    IConnectionProvider Get(string providerId);
}

public sealed class ConnectionProviderRegistry : IConnectionProviderRegistry
{
    private readonly FrozenDictionary<string, IConnectionProvider> _providers;

    public ConnectionProviderRegistry(IEnumerable<IConnectionProvider> providers) =>
        _providers = providers.ToFrozenDictionary(provider => provider.ProviderId, StringComparer.Ordinal);

    public IConnectionProvider Get(string providerId) =>
        _providers.GetValueOrDefault(providerId) ?? throw new UnsupportedConnectionProviderException();
}

public sealed class UnsupportedConnectionProviderException : InvalidOperationException
{
    public UnsupportedConnectionProviderException() : base("The connection provider is unsupported.") { }
    public string Code => "unsupported_provider";
}
