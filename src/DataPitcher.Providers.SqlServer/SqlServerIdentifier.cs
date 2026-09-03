using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.Providers.SqlServer;

public static class SqlServerIdentifier
{
    public static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
