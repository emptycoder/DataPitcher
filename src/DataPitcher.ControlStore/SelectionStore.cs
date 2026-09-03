using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Selection;
using DataPitcher.Core.Time;
using Microsoft.Data.Sqlite;

namespace DataPitcher.ControlStore;

public sealed class SelectionStore(ControlDatabase database, IClock clock) : ISelectionRepository
{
    private const string SelectColumns =
        "SELECT SelectionId, DisplayName, QueryJson, Version, UpdatedUtc, ConnectionId, SnapshotId, RootSchema, RootTable, StableKeyConstraintName, StableKeyColumnsJson FROM Selections";

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
        var stableKeyColumnsJson = stableKeyColumns is null ? null : JsonSerializer.Serialize(stableKeyColumns);
        var existing = Find(db, selectionId);
        if (existing is null)
        {
            var row = new Row(
                selectionId.ToString(),
                displayName,
                queryJson,
                1,
                now,
                connectionId?.ToString(),
                snapshotId?.ToString(),
                rootSchema,
                rootTable,
                stableKeyConstraintName,
                stableKeyColumnsJson
            );
            db.Execute(
                "INSERT INTO Selections (SelectionId, DisplayName, QueryJson, Version, CreatedUtc, UpdatedUtc, ConnectionId, SnapshotId, RootSchema, RootTable, StableKeyConstraintName, StableKeyColumnsJson) VALUES (@selectionId, @displayName, @queryJson, @version, @createdUtc, @updatedUtc, @connectionId, @snapshotId, @rootSchema, @rootTable, @stableKeyConstraintName, @stableKeyColumnsJson)",
                new ControlParameter("selectionId", row.SelectionId),
                new ControlParameter("displayName", row.DisplayName),
                new ControlParameter("queryJson", row.QueryJson),
                new ControlParameter("version", row.Version),
                new ControlParameter("createdUtc", now),
                new ControlParameter("updatedUtc", now),
                new ControlParameter("connectionId", row.ConnectionId),
                new ControlParameter("snapshotId", row.SnapshotId),
                new ControlParameter("rootSchema", row.RootSchema),
                new ControlParameter("rootTable", row.RootTable),
                new ControlParameter("stableKeyConstraintName", row.StableKeyConstraintName),
                new ControlParameter("stableKeyColumnsJson", row.StableKeyColumnsJson)
            );
            transaction.Commit();
            return Task.FromResult(ToRecord(row));
        }
        if (existing.Version != ParseVersion(ifMatch))
            throw new SelectionVersionMismatchException();
        var affected = db.Execute(
            "UPDATE Selections SET DisplayName = @displayName, QueryJson = @queryJson, ConnectionId = @connectionId, SnapshotId = @snapshotId, RootSchema = @rootSchema, RootTable = @rootTable, StableKeyConstraintName = @stableKeyConstraintName, StableKeyColumnsJson = @stableKeyColumnsJson, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE SelectionId = @selectionId AND Version = @version",
            new ControlParameter("displayName", displayName),
            new ControlParameter("queryJson", queryJson),
            new ControlParameter("connectionId", connectionId?.ToString()),
            new ControlParameter("snapshotId", snapshotId?.ToString()),
            new ControlParameter("rootSchema", rootSchema),
            new ControlParameter("rootTable", rootTable),
            new ControlParameter("stableKeyConstraintName", stableKeyConstraintName),
            new ControlParameter("stableKeyColumnsJson", stableKeyColumnsJson),
            new ControlParameter("updatedUtc", now),
            new ControlParameter("selectionId", selectionId.ToString()),
            new ControlParameter("version", existing.Version)
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
            new ControlParameter("selectionId", selectionId.ToString()),
            new ControlParameter("version", ParseVersion(ifMatch))
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
        var row = Find(db, selectionId);
        return Task.FromResult(row is null ? null : ToRecord(row));
    }

    public Task<IReadOnlyList<SelectionRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        IReadOnlyList<SelectionRecord> records = db.Query(SelectColumns, Map)
            .OrderBy(row => row.DisplayName, StringComparer.Ordinal)
            .ThenBy(row => row.SelectionId, StringComparer.Ordinal)
            .Select(ToRecord)
            .ToArray();
        return Task.FromResult(records);
    }

    private static Row? Find(ControlConnection db, Guid selectionId) =>
        db.Single(
            SelectColumns + " WHERE SelectionId = @selectionId",
            Map,
            new ControlParameter("selectionId", selectionId.ToString())
        );

    private static Row Map(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)
        );

    private static SelectionRecord ToRecord(Row row) =>
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

    private sealed record Row(
        string SelectionId,
        string DisplayName,
        string QueryJson,
        long Version,
        string UpdatedUtc,
        string? ConnectionId,
        string? SnapshotId,
        string? RootSchema,
        string? RootTable,
        string? StableKeyConstraintName,
        string? StableKeyColumnsJson
    );
}
