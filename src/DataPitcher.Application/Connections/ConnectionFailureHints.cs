namespace DataPitcher.Application.Connections;

/// <summary>
/// Turns well-known driver login failures into an explanation an operator can act on. The driver text stays as the
/// error; the hint is added as a note so nothing is hidden.
/// </summary>
public static class ConnectionFailureHints
{
    public static string? Explain(string driverMessage)
    {
        if (driverMessage.Contains("token-identified principal", StringComparison.OrdinalIgnoreCase))
            return "The Entra ID sign-in worked, but the account that signed in has no user in the target database. "
                + "In that database run CREATE USER [name@domain] FROM EXTERNAL PROVIDER and grant it SELECT, "
                + "or sign in with an account that already has one. Also check that the Database in the connection "
                + "string is the one where the user exists, and that the browser prompt used the intended account.";
        if (driverMessage.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase))
            return "The server rejected the login: the password is wrong, the login is disabled, or it is not mapped "
                + "to a user in the target database.";
        if (driverMessage.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase))
            return "The server was reached but the named database does not exist or the login has no access to it.";
        if (
            driverMessage.Contains("network-related", StringComparison.OrdinalIgnoreCase)
            || driverMessage.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
        )
            return "The server could not be reached from the API host. Check the host, port, firewall, and that "
                + "the instance accepts remote connections.";
        if (driverMessage.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase))
            return "PostgreSQL rejected the password for this role, or pg_hba.conf does not allow this client.";
        return null;
    }
}
