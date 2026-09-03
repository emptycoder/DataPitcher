namespace DataPitcher.Core.Selection;

public static class RawSqlSafetyValidator
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER",
        "ANALYZE",
        "CALL",
        "COMMIT",
        "COPY",
        "CREATE",
        "DECLARE",
        "DELETE",
        "DO",
        "DROP",
        "EXEC",
        "EXECUTE",
        "GRANT",
        "INSERT",
        "LOCK",
        "MERGE",
        "REVOKE",
        "ROLLBACK",
        "SET",
        "TRUNCATE",
        "UPDATE",
        "USE",
        "VACUUM",
    };

    public static void Validate(RawSqlDialect dialect, string sql)
    {
        var tokens = Tokens(sql);
        if (dialect == RawSqlDialect.SqlServer && tokens.Any(token => EqualsToken(token.Text, "GO")))
            throw new RawSqlValidationException("SQL Server batch separators are not allowed.");
        if (tokens.Count == 0 || (!EqualsToken(tokens[0].Text, "SELECT") && !EqualsToken(tokens[0].Text, "WITH")))
            throw new RawSqlValidationException("Raw SQL must start with SELECT or WITH.");

        var separators = tokens
            .Select((token, index) => (token, index))
            .Where(pair => string.Equals(pair.token.Text, ";", StringComparison.Ordinal))
            .ToArray();
        if (separators.Length > 1 || separators.Length == 1 && separators[0].index != tokens.Count - 1)
            throw new RawSqlValidationException("Raw SQL may contain only one statement.");

        foreach (var token in tokens.Where(token => !string.Equals(token.Text, ";", StringComparison.Ordinal)))
        {
            if (Forbidden.Contains(token.Text))
                throw new RawSqlValidationException(
                    "Raw SQL contains a data-modifying token: " + token.Text.ToUpperInvariant() + "."
                );
            if (EqualsToken(token.Text, "INTO"))
                throw new RawSqlValidationException("Raw SQL contains a data-modifying token: INTO.");
        }
    }

    public static string RemoveTrailingOrderBy(string sql)
    {
        var tokens = Tokens(sql);
        var terminator = tokens.LastOrDefault(token =>
            token.Depth == 0 && string.Equals(token.Text, ";", StringComparison.Ordinal)
        );
        var end = terminator?.Start ?? sql.Length;
        var order = tokens
            .TakeWhile(token => token.Start < end)
            .Select((token, index) => (token, index))
            .LastOrDefault(pair =>
                pair.token.Depth == 0
                && EqualsToken(pair.token.Text, "ORDER")
                && pair.index + 1 < tokens.Count
                && tokens[pair.index + 1].Start < end
                && tokens[pair.index + 1].Depth == 0
                && EqualsToken(tokens[pair.index + 1].Text, "BY")
            );
        return order.token is not null ? sql[..order.token.Start].TrimEnd()
            : terminator is not null ? sql[..terminator.Start].TrimEnd()
            : sql;
    }

    public static bool TrySplitLeadingCte(string sql, out string ctes, out string query)
    {
        var tokens = Tokens(sql);
        var select = tokens.FirstOrDefault(token => token.Depth == 0 && EqualsToken(token.Text, "SELECT"));
        if (tokens.Count == 0 || !EqualsToken(tokens[0].Text, "WITH") || select is null)
        {
            ctes = string.Empty;
            query = string.Empty;
            return false;
        }

        ctes = sql[..select.Start];
        query = sql[select.Start..];
        return true;
    }

    private static bool EqualsToken(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static List<Token> Tokens(string sql)
    {
        var result = new List<Token>();
        for (int index = 0, depth = 0; index < sql.Length; )
        {
            var value = sql[index];
            if (char.IsWhiteSpace(value))
            {
                index++;
                continue;
            }
            if (value == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index = SkipLine(sql, index + 2);
                continue;
            }
            if (value == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlock(sql, index + 2);
                continue;
            }
            if (value == '\'' || value == '"')
            {
                index = SkipQuoted(sql, index, value);
                continue;
            }
            if (value == '[')
            {
                index = SkipBracket(sql, index + 1);
                continue;
            }
            if (value == '(')
            {
                depth++;
                index++;
                continue;
            }
            if (value == ')')
            {
                depth--;
                index++;
                continue;
            }
            if (value == ';')
            {
                result.Add(new Token(";", index, depth));
                index++;
                continue;
            }
            if (char.IsLetter(value) || value == '_')
            {
                var start = index++;
                while (
                    index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_' || sql[index] == '$')
                )
                    index++;
                result.Add(new Token(sql[start..index], start, depth));
                continue;
            }
            index++;
        }
        return result;
    }

    private sealed record Token(string Text, int Start, int Depth);

    private static int SkipLine(string sql, int index)
    {
        while (index < sql.Length && sql[index] != '\n')
            index++;
        return index;
    }

    private static int SkipBlock(string sql, int index)
    {
        var depth = 1;
        while (index + 1 < sql.Length && depth > 0)
        {
            if (sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
                index++;
        }
        if (depth != 0)
            throw new RawSqlValidationException("Raw SQL has an unterminated block comment.");
        return index;
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] == quote)
            {
                if (index + 1 < sql.Length && sql[index + 1] == quote)
                {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        throw new RawSqlValidationException("Raw SQL has an unterminated quoted value.");
    }

    private static int SkipBracket(string sql, int index)
    {
        while (index < sql.Length)
        {
            if (sql[index] == ']')
            {
                if (index + 1 < sql.Length && sql[index + 1] == ']')
                {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        throw new RawSqlValidationException("Raw SQL has an unterminated bracket identifier.");
    }
}
