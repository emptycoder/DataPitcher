using DataPitcher.Core.Connections;
using DataPitcher.Infrastructure.Connections;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class FileSecretStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "datapitcher-secrets-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task StoreAsync_WritesAMountedSecretTheResolverCanRead()
    {
        var store = new FileSecretStore(root);
        var credentialId = Guid.NewGuid();

        var reference = await store.StoreAsync(credentialId, "Server=localhost;Database=app", CancellationToken.None);
        var resolved = await new SecretReferenceResolver(root).ResolveAsync(reference, CancellationToken.None);

        Assert.Equal(SecretReferenceKind.FileMounted, reference.Kind);
        Assert.StartsWith(Path.GetFullPath(root), reference.Locator, StringComparison.Ordinal);
        Assert.Equal("Server=localhost;Database=app", resolved);
    }

    [Fact]
    public async Task RemoveAsync_DeletesOnlyMountedSecretsInsideTheRoot()
    {
        var store = new FileSecretStore(root);
        var reference = await store.StoreAsync(Guid.NewGuid(), "secret", CancellationToken.None);
        var outside = Path.Combine(Path.GetTempPath(), "datapitcher-outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, "keep");

        await store.RemoveAsync(reference, CancellationToken.None);
        await store.RemoveAsync(new SecretReference(SecretReferenceKind.FileMounted, outside), CancellationToken.None);
        await store.RemoveAsync(
            new SecretReference(SecretReferenceKind.EnvironmentVariable, "X"),
            CancellationToken.None
        );

        Assert.False(File.Exists(reference.Locator));
        Assert.True(File.Exists(outside));
        File.Delete(outside);
    }

    [Fact]
    public async Task StoreAsync_RejectsEmptySecrets()
    {
        var store = new FileSecretStore(root);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreAsync(Guid.NewGuid(), " ", CancellationToken.None)
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
