namespace DataPitcher.Providers.SqlServer;

public static class SqlServerIdentifier
{
    public static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
