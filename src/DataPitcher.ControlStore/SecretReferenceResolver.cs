using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.ControlStore;

public sealed class SecretReferenceResolver(string secretsRoot) : ISecretReferenceResolver
{
    public Task<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return reference.Kind switch
        {
            SecretReferenceKind.EnvironmentVariable => Task.FromResult(
                Environment.GetEnvironmentVariable(reference.Locator)
                    ?? throw new InvalidOperationException("Configured secret is unavailable.")
            ),
            SecretReferenceKind.FileMounted => ReadMountedAsync(reference.Locator, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(reference)),
        };
    }

    private async Task<string> ReadMountedAsync(string locator, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(secretsRoot));
        var path = Path.GetFullPath(locator);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Mounted secret reference is outside the configured root.");
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Configured secret is unavailable.");
        }
    }
}
