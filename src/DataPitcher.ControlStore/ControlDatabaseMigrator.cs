using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;

namespace DataPitcher.ControlStore;

public sealed class ControlDatabaseMigrator(ControlDatabase database, IClock clock)
{
    private static readonly (int Version, string Resource)[] Scripts =
    [
        (1, "DataPitcher.ControlStore.Migrations.0001-initial.sql"),
        (2, "DataPitcher.ControlStore.Migrations.0002-job-recovery.sql"),
        (3, "DataPitcher.ControlStore.Migrations.0003-job-events.sql"),
        (4, "DataPitcher.ControlStore.Migrations.0004-connections-and-schema.sql"),
        (5, "DataPitcher.ControlStore.Migrations.0005-selections-and-plans.sql"),
        (6, "DataPitcher.ControlStore.Migrations.0006-plan-content.sql"),
        (7, "DataPitcher.ControlStore.Migrations.0007-selection-connection-and-snapshot.sql"),
        (8, "DataPitcher.ControlStore.Migrations.0008-plan-associations.sql"),
        (9, "DataPitcher.ControlStore.Migrations.0009-selection-root-identity.sql"),
    ];

    static ControlDatabaseMigrator()
    {
        foreach (var script in Scripts)
        {
            ArgumentNullException.ThrowIfNull(
                typeof(ControlDatabaseMigrator).Assembly.GetManifestResourceInfo(script.Resource),
                $"Missing migration resource: {script.Resource}"
            );
        }
    }

    public void Apply()
    {
        using var db = database.Open();
        db.Execute(
            "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, AppliedUtc TEXT NOT NULL);"
        );
        var applied = db.Query<int>("SELECT Version FROM SchemaVersion").ToHashSet();
        foreach (var script in Scripts.Where(script => !applied.Contains(script.Version)))
        {
            using var transaction = db.BeginTransaction();
            db.Execute(ReadScript(script.Resource));
            db.Execute(
                "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (@version, @appliedUtc)",
                new ControlParameter("version", script.Version),
                new ControlParameter("appliedUtc", clock.UtcNow.ToString("O"))
            );
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
