using DataPitcher.Application.Connections;
using Xunit;

namespace DataPitcher.UnitTests.Connections;

public sealed class ConnectionFailureHintsTests
{
    [Fact]
    public void Explain_TokenIdentifiedPrincipal_PointsAtTheMissingDatabaseUser()
    {
        var hint = ConnectionFailureHints.Explain("Login failed for user '<token-identified principal>'.");

        Assert.NotNull(hint);
        Assert.Contains("FROM EXTERNAL PROVIDER", hint);
    }

    [Theory]
    [InlineData("Login failed for user 'sa'.", "rejected the login")]
    [InlineData("Cannot open database \"Shop\" requested by the login.", "does not exist")]
    [InlineData("A network-related or instance-specific error occurred", "could not be reached")]
    [InlineData("28P01: password authentication failed for user \"app\"", "PostgreSQL rejected")]
    public void Explain_KnownDriverErrors_GetAHint(string message, string expected) =>
        Assert.Contains(expected, ConnectionFailureHints.Explain(message)!);

    [Fact]
    public void Explain_UnknownErrors_ReturnNull() => Assert.Null(ConnectionFailureHints.Explain("Something odd."));
}
