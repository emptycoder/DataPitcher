using DataPitcher.Core.Connections;

namespace DataPitcher.Infrastructure.Connections;

/// <summary>
/// Writes connection secrets supplied by an operator into the configured secrets root so that
/// <see cref="SecretReferenceResolver"/> can read them back as mounted secrets. Secrets never enter the control
/// database and are never returned by the API.
/// </summary>
public interface ISecretWriter
{
    Task<SecretReference> StoreAsync(Guid credentialId, string secret, CancellationToken cancellationToken);
    Task RemoveAsync(SecretReference reference, CancellationToken cancellationToken);
}

public sealed class FileSecretStore(string secretsRoot) : ISecretWriter
{
    private readonly string root = Path.GetFullPath(secretsRoot);

    public async Task<SecretReference> StoreAsync(Guid credentialId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "connection-" + credentialId.ToString("N") + ".secret");
        await File.WriteAllTextAsync(path, secret, cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new SecretReference(SecretReferenceKind.FileMounted, path);
    }

    public Task RemoveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Kind is not SecretReferenceKind.FileMounted)
            return Task.CompletedTask;
        var path = Path.GetFullPath(reference.Locator);
        if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }
}
