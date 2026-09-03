using System.Globalization;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Selections;

public sealed record SelectionRecord(Guid SelectionId, string DisplayName, string QueryJson, long Version, DateTimeOffset UpdatedUtc, Guid? ConnectionId = null, Guid? SnapshotId = null);

public sealed class SelectionVersionMismatchException : InvalidOperationException
{
    public SelectionVersionMismatchException() : base("Selection version does not match.") { }
}

public sealed class SelectionStore(ControlDatabase database, IClock clock)
{
    public Task<SelectionRecord> SaveAsync(Guid selectionId, string displayName, string queryJson, string ifMatch, CancellationToken cancellationToken, Guid? connectionId = null, Guid? snapshotId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var existing = db.GetTable<SelectionRow>().SingleOrDefault(row => row.SelectionId == selectionId.ToString());
        if (existing is null)
        {
            var row = new SelectionRow { SelectionId = selectionId.ToString(), DisplayName = displayName, QueryJson = queryJson, Version = 1, CreatedUtc = now, UpdatedUtc = now, ConnectionId = connectionId?.ToString(), SnapshotId = snapshotId?.ToString() };
            db.Insert(row);
            transaction.Commit();
            return Task.FromResult(ToRecord(row));
        }
        if (existing.Version != ParseVersion(ifMatch)) throw new SelectionVersionMismatchException();
        var affected = db.Execute(
            "UPDATE Selections SET DisplayName = @displayName, QueryJson = @queryJson, ConnectionId = @connectionId, SnapshotId = @snapshotId, Version = Version + 1, UpdatedUtc = @updatedUtc WHERE SelectionId = @selectionId AND Version = @version",
            new DataParameter("displayName", displayName), new DataParameter("queryJson", queryJson), new DataParameter("connectionId", connectionId?.ToString()), new DataParameter("snapshotId", snapshotId?.ToString()), new DataParameter("updatedUtc", now),
            new DataParameter("selectionId", selectionId.ToString()), new DataParameter("version", existing.Version));
        if (affected != 1) throw new SelectionVersionMismatchException();
        transaction.Commit();
        return Task.FromResult(new SelectionRecord(selectionId, displayName, queryJson, existing.Version + 1, clock.UtcNow, connectionId, snapshotId));
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
        IReadOnlyList<SelectionRecord> records = db.GetTable<SelectionRow>().ToArray()
            .OrderBy(row => row.DisplayName, StringComparer.Ordinal)
            .ThenBy(row => row.SelectionId, StringComparer.Ordinal)
            .Select(ToRecord)
            .ToArray();
        return Task.FromResult(records);
    }

    private static SelectionRecord ToRecord(SelectionRow row) =>
        new(Guid.Parse(row.SelectionId), row.DisplayName, row.QueryJson, row.Version, DateTimeOffset.Parse(row.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), row.ConnectionId is null ? null : Guid.Parse(row.ConnectionId), row.SnapshotId is null ? null : Guid.Parse(row.SnapshotId));

    private static long ParseVersion(string ifMatch) =>
        long.TryParse(ifMatch.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw new SelectionVersionMismatchException();

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
