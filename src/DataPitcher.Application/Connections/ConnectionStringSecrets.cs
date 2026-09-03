using System.Data.Common;
using System.Text;

namespace DataPitcher.Application.Connections;

/// <summary>
/// Separates the password from the rest of a provider connection string so the non-secret settings can be shown to an
/// operator for editing while the password itself stays on the API host. Works on the generic
/// <c>key=value;</c> syntax shared by SQL Server and PostgreSQL, including quoted values.
/// </summary>
public static class ConnectionStringSecrets
{
    private static readonly string[] PasswordKeywords = ["password", "pwd", "psw"];

    /// <summary>The connection string without any password-like entry, and whether one was present.</summary>
    public static (string ConnectionString, bool HasPassword) Redact(string connectionString)
    {
        var builder = Parse(connectionString);
        var hasPassword = ExtractPassword(builder) is not null;
        foreach (var key in builder.Keys!.Cast<string>().Where(IsSecretKeyword).ToArray())
            builder.Remove(key);
        return (builder.ConnectionString, hasPassword);
    }

    /// <summary>The first non-empty password entry, or null when the string carries none.</summary>
    public static string? ExtractPassword(string connectionString) => ExtractPassword(Parse(connectionString));

    /// <summary>Like <see cref="ExtractPassword(string)"/> but returns null for text that is not a connection string.</summary>
    public static string? TryExtractPassword(string connectionString)
    {
        try
        {
            return ExtractPassword(connectionString);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="connectionString"/> with <paramref name="password"/> appended unless the string already
    /// carries a non-empty password of its own. The original text is preserved verbatim.
    /// </summary>
    public static string WithPassword(string connectionString, string password)
    {
        if (ExtractPassword(connectionString) is not null)
            return connectionString;
        var result = new StringBuilder(connectionString.TrimEnd());
        if (result.Length > 0 && result[^1] != ';')
            result.Append(';');
        DbConnectionStringBuilder.AppendKeyValuePair(result, "Password", password);
        return result.ToString();
    }

    private static DbConnectionStringBuilder Parse(string connectionString)
    {
        try
        {
            return new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The stored connection string could not be parsed.", exception);
        }
    }

    private static string? ExtractPassword(DbConnectionStringBuilder builder)
    {
        foreach (var keyword in PasswordKeywords)
            if (builder.TryGetValue(keyword, out var value) && value is string { Length: > 0 } password)
                return password;
        return null;
    }

    private static bool IsSecretKeyword(string key)
    {
        var normalized = key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "password" or "pwd" or "psw" || normalized.EndsWith("password", StringComparison.Ordinal);
    }
}
