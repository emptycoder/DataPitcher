using LinqToDB.Data;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;

namespace DataPitcher.Infrastructure.Migrations;

public sealed class ControlDatabaseMigrator(ControlDatabase database, IClock clock)
{
    private static readonly (int Version, string Resource)[] Scripts = [(1, "DataPitcher.Infrastructure.Migrations.0001-initial.sql"), (2, "DataPitcher.Infrastructure.Migrations.0002-job-recovery.sql"), (3, "DataPitcher.Infrastructure.Migrations.0003-job-events.sql"), (4, "DataPitcher.Infrastructure.Migrations.0004-connections-and-schema.sql"), (5, "DataPitcher.Infrastructure.Migrations.0005-selections-and-plans.sql")];
    static ControlDatabaseMigrator()
    {
        foreach (var script in Scripts)
        {
            ArgumentNullException.ThrowIfNull(typeof(ControlDatabaseMigrator).Assembly.GetManifestResourceInfo(script.Resource), $"Missing migration resource: {script.Resource}");
        }
    }
    public void Apply()
    {
        using var db = database.Open();
        db.Execute("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, AppliedUtc TEXT NOT NULL);");
        var applied = db.Query<int>("SELECT Version FROM SchemaVersion").ToHashSet();
        foreach (var script in Scripts.Where(script => !applied.Contains(script.Version)))
        {
            using var transaction = db.BeginTransaction();
            db.Execute(ReadScript(script.Resource));
            db.Execute("INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (@version, @appliedUtc)", new DataParameter("version", script.Version), new DataParameter("appliedUtc", clock.UtcNow.ToString("O")));
            transaction.Commit();
        }
    }
    private static string ReadScript(string resource)
    {
        using var stream = typeof(ControlDatabaseMigrator).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
