using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataPitcher.UnitTests.Connections;

public sealed class ConnectionProviderTests
{
    [Fact]
    public void PostgreSqlConnectionProvider_UsesTheExistingProbeAndIntrospector()
    {
        var provider = new PostgreSqlConnectionProvider();

        Assert.Equal("postgresql", provider.ProviderId);
        Assert.IsType<PostgreSqlConnectionProbe>(provider.CapabilityDetector);
        Assert.IsType<PostgreSqlSchemaIntrospector>(provider.SchemaIntrospector);
    }

    [Fact]
    public void SqlServerConnectionProvider_UsesTheExistingProbeAndIntrospector()
    {
        var provider = new SqlServerConnectionProvider();

        Assert.Equal("sqlserver", provider.ProviderId);
        Assert.IsType<SqlServerConnectionProbe>(provider.CapabilityDetector);
        Assert.IsType<SqlServerSchemaIntrospector>(provider.SchemaIntrospector);
    }

    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    public void SqlServerEntraAuthentication_RegistersAProviderForEveryEntraLogin(SqlAuthenticationMethod method)
    {
        SqlServerEntraAuthentication.EnsureRegistered();

        Assert.IsType<ActiveDirectoryAuthenticationProvider>(SqlAuthenticationProvider.GetProvider(method));
    }
}
