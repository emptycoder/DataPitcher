namespace DataPitcher.Providers.PostgreSql;

public static class PostgreSqlIdentifier
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
