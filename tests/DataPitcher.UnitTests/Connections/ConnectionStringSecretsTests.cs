using DataPitcher.Application.Connections;
using Xunit;

namespace DataPitcher.UnitTests.Connections;

public sealed class ConnectionStringSecretsTests
{
    [Theory]
    [InlineData("Server=db,1433;Database=app;User Id=sa;Password=p@ss;Encrypt=True", "p@ss")]
    [InlineData("Host=db;Database=app;Username=app;Pwd=\"p;w\"", "p;w")]
    [InlineData("Host=db;Database=app;PSW='it''s'", "it's")]
    public void ExtractPassword_ReadsEveryProviderKeyword(string connectionString, string expected) =>
        Assert.Equal(expected, ConnectionStringSecrets.ExtractPassword(connectionString));

    [Theory]
    [InlineData("Server=db;Database=app;Integrated Security=True")]
    [InlineData("Host=db;Database=app;Username=app;Password=")]
    public void ExtractPassword_WhenAbsentOrEmpty_ReturnsNull(string connectionString) =>
        Assert.Null(ConnectionStringSecrets.ExtractPassword(connectionString));

    [Fact]
    public void Redact_RemovesPasswordsAndReportsThatOneExisted()
    {
        var (redacted, hasPassword) = ConnectionStringSecrets.Redact(
            "Server=db,1433;Database=app;User Id=sa;Password=\"p;w\";SSL Password=cert;Encrypt=True"
        );

        Assert.True(hasPassword);
        Assert.DoesNotContain("p;w", redacted);
        Assert.DoesNotContain("cert", redacted);
        Assert.Contains("server=db,1433", redacted);
        Assert.Contains("user id=sa", redacted);
        Assert.Contains("encrypt=True", redacted);
    }

    [Fact]
    public void Redact_WithoutPassword_KeepsEverythingElse()
    {
        var (redacted, hasPassword) = ConnectionStringSecrets.Redact("Server=db;Database=app;Integrated Security=SSPI");

        Assert.False(hasPassword);
        Assert.Equal("server=db;database=app;integrated security=SSPI", redacted);
    }

    [Fact]
    public void Redact_WhenUnparseable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => ConnectionStringSecrets.Redact("=;=broken"));

    [Fact]
    public void WithPassword_AppendsAQuotedPasswordVerbatimToTheOriginalText()
    {
        var merged = ConnectionStringSecrets.WithPassword("Server=db;Database=app;User Id=sa;Encrypt=True", "p;\"w");

        Assert.Equal("Server=db;Database=app;User Id=sa;Encrypt=True;Password='p;\"w'", merged);
        Assert.Equal("p;\"w", ConnectionStringSecrets.ExtractPassword(merged));
    }

    [Fact]
    public void WithPassword_WhenTheStringAlreadyHasOne_LeavesItAlone()
    {
        const string original = "Server=db;Database=app;User Id=sa;Password=mine;";

        Assert.Same(original, ConnectionStringSecrets.WithPassword(original, "stored"));
    }

    [Fact]
    public void WithPassword_ReplacesAnEmptyPasswordEntry()
    {
        var merged = ConnectionStringSecrets.WithPassword("Server=db;Database=app;User Id=sa;Password=", "stored");

        Assert.Equal("stored", ConnectionStringSecrets.ExtractPassword(merged));
    }
}
