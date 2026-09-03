using DataPitcher.Core.Connections;
using DataPitcher.Infrastructure.Connections;
using Xunit;

namespace DataPitcher.UnitTests.Infrastructure;

public sealed class SecretReferenceResolverTests
{
    [Fact]
    public async Task SecretReferenceResolver_WhenEnvironmentVariableExists_ReturnsItsValue()
    {
        const string name = "DP_TEST_RESOLVER_SECRET";
        const string value = "password-redaction-sentinel";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var resolver = new SecretReferenceResolver(Path.GetTempPath());

            var resolved = await resolver.ResolveAsync(
                new(SecretReferenceKind.EnvironmentVariable, name),
                CancellationToken.None
            );

            Assert.Equal(value, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task SecretReferenceResolver_WhenEnvironmentVariableIsMissing_UsesAFixedSafeError()
    {
        var resolver = new SecretReferenceResolver(Path.GetTempPath());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                new(SecretReferenceKind.EnvironmentVariable, "DP_TEST_MISSING_SECRET"),
                CancellationToken.None
            )
        );

        Assert.Equal("Configured secret is unavailable.", exception.Message);
    }

    [Fact]
    public async Task SecretReferenceResolver_WhenMountedFileIsInsideTheRoot_ReturnsItsContent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datapitcher-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "database-secret");
        await File.WriteAllTextAsync(path, "reference-content-sentinel");
        try
        {
            var resolver = new SecretReferenceResolver(root);

            var resolved = await resolver.ResolveAsync(
                new(SecretReferenceKind.FileMounted, path),
                CancellationToken.None
            );

            Assert.Equal("reference-content-sentinel", resolved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SecretReferenceResolver_WhenMountedFileEscapesTheRoot_UsesAFixedSafeError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datapitcher-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(outside, "reference-content-sentinel");
        try
        {
            var resolver = new SecretReferenceResolver(root);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(new(SecretReferenceKind.FileMounted, outside), CancellationToken.None)
            );

            Assert.Equal("Mounted secret reference is outside the configured root.", exception.Message);
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SecretReferenceResolver_WhenMountedFileIsMissing_DoesNotExposeThePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datapitcher-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "missing-secret");
        try
        {
            var resolver = new SecretReferenceResolver(root);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                resolver.ResolveAsync(new(SecretReferenceKind.FileMounted, missing), CancellationToken.None)
            );

            Assert.DoesNotContain(missing, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SecretReferenceResolver_WhenReferenceKindIsUnknown_RejectsIt()
    {
        var resolver = new SecretReferenceResolver(Path.GetTempPath());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            resolver.ResolveAsync(new((SecretReferenceKind)99, "ignored"), CancellationToken.None)
        );
    }
}
