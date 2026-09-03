using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Connections;
using DataPitcher.Core.Jobs;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Schema;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using DataPitcher.Core.Transfer;
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.ControlStore;

public sealed class SelectionStore(ControlDatabase database, IClock clock) : ISelectionRepository
{
    public Task<SelectionRecord> SaveAsync(
        Guid selectionId,
        string displayName,
        string queryJson,
        string ifMatch,
        CancellationToken cancellationToken,
        Guid? connectionId = null,
        Guid? snapshotId = null,
        string? rootSchema = null,
        string? rootTable = null,
        string? stableKeyConstraintName = null,
        IReadOnlyList<string>? stableKeyColumns = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var existing = db.GetTable<SelectionRow>().SingleOrDefault(row => row.SelectionId == selectionId.ToString());
        if (existing is null)
        {
            var row = new SelectionRow
            {
                SelectionId = selectionId.ToString(),
                DisplayName = displayName,
                QueryJson = queryJson,
                Version = 1,
                CreatedUtc = now,
                UpdatedUtc = now,
                ConnectionId = connectionId?.ToString(),
                SnapshotId = snapshotId?.ToString(),
                RootSchema = rootSchema,
                RootTable = rootTable,
                StableKeyConstraintName = stableKeyConstraintName,
                StableKeyColumnsJson = stableKeyColumns is null ? null : JsonSerializer.Serialize(stableKeyColumns),
            };
            db.Insert(row);
            transaction.Commit();
            return Task.FromResult(ToRecord(row));
        }
        if (existing.Version != ParseVersion(ifMatch))
            throw new SelectionVersionMismatchException();
        var affected = db.Execute(
            "UPDATE Selections SET DisplayName = @displayName, QueryJson = @queryJson, ConnectionId = @connectionId, SnapshotId = @snapshotId, RootSchema = @rootSchema, RootTable = @rootTable, StableKeyConstraintName = @stableKeyConstraintName, StableKeyColumnsJson = @stableKeyColumnsJson, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE SelectionId = @selectionId AND Version = @version",
            new DataParameter("displayName", displayName),
            new DataParameter("queryJson", queryJson),
            new DataParameter("connectionId", connectionId?.ToString()),
            new DataParameter("snapshotId", snapshotId?.ToString()),
            new DataParameter("rootSchema", rootSchema),
            new DataParameter("rootTable", rootTable),
            new DataParameter("stableKeyConstraintName", stableKeyConstraintName),
            new DataParameter(
                "stableKeyColumnsJson",
                stableKeyColumns is null ? null : JsonSerializer.Serialize(stableKeyColumns)
            ),
            new DataParameter("updatedUtc", now),
            new DataParameter("selectionId", selectionId.ToString()),
            new DataParameter("version", existing.Version)
        );
        if (affected != 1)
            throw new SelectionVersionMismatchException();
        transaction.Commit();
        return Task.FromResult(
            new SelectionRecord(
                selectionId,
                displayName,
                queryJson,
                existing.Version + 1,
                clock.UtcNow,
                connectionId,
                snapshotId,
                rootSchema,
                rootTable,
                stableKeyConstraintName,
                stableKeyColumns
            )
        );
    }

    public Task DeleteAsync(Guid selectionId, string ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var affected = db.Execute(
            "DELETE FROM Selections WHERE SelectionId = @selectionId AND Version = @version",
            new DataParameter("selectionId", selectionId.ToString()),
            new DataParameter("version", ParseVersion(ifMatch))
        );
        if (affected != 1)
            throw new SelectionVersionMismatchException();
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<SelectionRecord?> FindAsync(Guid selectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.GetTable<SelectionRow>().SingleOrDefault(row => row.SelectionId == selectionId.ToString());
        return Task.FromResult(row is null ? null : ToRecord(row));
    }

    public Task<IReadOnlyList<SelectionRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        IReadOnlyList<SelectionRecord> records = db.GetTable<SelectionRow>()
            .ToArray()
            .OrderBy(row => row.DisplayName, StringComparer.Ordinal)
            .ThenBy(row => row.SelectionId, StringComparer.Ordinal)
            .Select(ToRecord)
            .ToArray();
        return Task.FromResult(records);
    }

    private static SelectionRecord ToRecord(SelectionRow row) =>
        new(
            Guid.Parse(row.SelectionId),
            row.DisplayName,
            row.QueryJson,
            row.Version,
            DateTimeOffset.Parse(row.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            row.ConnectionId is null ? null : Guid.Parse(row.ConnectionId),
            row.SnapshotId is null ? null : Guid.Parse(row.SnapshotId),
            row.RootSchema,
            row.RootTable,
            row.StableKeyConstraintName,
            row.StableKeyColumnsJson is null ? null : JsonSerializer.Deserialize<string[]>(row.StableKeyColumnsJson)
        );

    private static long ParseVersion(string ifMatch) =>
        long.TryParse(ifMatch.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
        && version > 0
            ? version
            : throw new SelectionVersionMismatchException();

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
