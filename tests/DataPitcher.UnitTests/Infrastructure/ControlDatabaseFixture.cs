using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB.Data;

namespace DataPitcher.UnitTests.Infrastructure;

internal sealed class ManualClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
}

internal sealed class ControlDatabaseFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"datapitcher-{Guid.NewGuid():N}.db");
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
    public ControlDatabase Database { get; }
    public ControlDatabaseMigrator Migrator { get; }

    public ControlDatabaseFixture()
    {
        Database = new($"Data Source={_path}");
        Migrator = new(Database, Clock);
    }

    public Guid SeedJob()
    {
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var stamp = Clock.UtcNow.ToString("O");
        using var db = Database.Open();
        db.Execute(
            $"INSERT INTO Jobs (JobId, RunId, PlanId, IdempotencyKey, State, CreatedUtc, UpdatedUtc) VALUES ('{jobId}', '{runId}', '{Guid.NewGuid()}', '{Guid.NewGuid():N}', 'Queued', '{stamp}', '{stamp}');"
        );
        db.Execute(
            $"INSERT INTO JobLeases (JobId, OwnerId, ExpiresUtc, FenceToken) VALUES ('{jobId}', NULL, NULL, 0);"
        );
        return jobId;
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
