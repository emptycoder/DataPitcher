using Microsoft.Data.SqlClient;

namespace DataPitcher.Providers.SqlServer;

/// <summary>
/// Registers the Microsoft Entra ID authentication provider with SqlClient. Since Microsoft.Data.SqlClient 7 the
/// provider ships in the separate Microsoft.Data.SqlClient.Extensions.Azure package and must be registered before a
/// connection string with <c>Authentication=Active Directory …</c> can open; otherwise the driver fails with
/// "Cannot find an authentication provider for 'ActiveDirectory…'".
/// </summary>
public static class SqlServerEntraAuthentication
{
#pragma warning disable CS0618 // Entra password login is deprecated by Microsoft but still offered to operators.
    private static readonly SqlAuthenticationMethod[] EntraMethods =
    [
        SqlAuthenticationMethod.ActiveDirectoryPassword,
        SqlAuthenticationMethod.ActiveDirectoryIntegrated,
        SqlAuthenticationMethod.ActiveDirectoryInteractive,
        SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
        SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
        SqlAuthenticationMethod.ActiveDirectoryManagedIdentity,
        SqlAuthenticationMethod.ActiveDirectoryMSI,
        SqlAuthenticationMethod.ActiveDirectoryDefault,
        SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity,
    ];
#pragma warning restore CS0618

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <summary>
    /// Registers the provider once. Every SQL Server entry point (provider, probe, catalog reader, sealing, run
    /// sessions) calls this from its static constructor so no connection can open before registration.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (registered)
                return;
            var provider = new ActiveDirectoryAuthenticationProvider();
            foreach (var method in EntraMethods)
                if (SqlAuthenticationProvider.GetProvider(method) is null)
                    SqlAuthenticationProvider.SetProvider(method, provider);
            registered = true;
        }
    }
}
