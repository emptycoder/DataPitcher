using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Plans;
using DataPitcher.Core.Time;
using Microsoft.Data.Sqlite;

namespace DataPitcher.ControlStore;

public sealed class PlanStore(ControlDatabase database, IClock clock) : IPlanRepository
{
    private const string SelectColumns =
        "SELECT PlanId, DisplayName, OperatorNote, Version, CanonicalHash, UpdatedUtc, SelectionId, SourceConnectionId, TargetConnectionId, SealFailureCode, SealFailureDetail FROM Plans";

    public Task<PlanRecord> SaveAsync(
        Guid planId,
        string displayName,
        string? operatorNote,
        string ifMatch,
        CancellationToken cancellationToken,
        Guid? selectionId = null,
        Guid? sourceConnectionId = null,
        Guid? targetConnectionId = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var existing = Find(db, planId);
        if (existing is null)
        {
            var row = new Row(
                planId.ToString(),
                displayName,
                operatorNote,
                1,
                null,
                now,
                selectionId?.ToString(),
                sourceConnectionId?.ToString(),
                targetConnectionId?.ToString(),
                null,
                null
            );
            db.Execute(
                "INSERT INTO Plans (PlanId, DisplayName, OperatorNote, Version, CanonicalHash, ContentJson, SealedUtc, CreatedUtc, UpdatedUtc, SelectionId, SourceConnectionId, TargetConnectionId) VALUES (@planId, @displayName, @operatorNote, @version, NULL, NULL, NULL, @createdUtc, @updatedUtc, @selectionId, @sourceConnectionId, @targetConnectionId)",
                new ControlParameter("planId", row.PlanId),
                new ControlParameter("displayName", row.DisplayName),
                new ControlParameter("operatorNote", row.OperatorNote),
                new ControlParameter("version", row.Version),
                new ControlParameter("createdUtc", now),
                new ControlParameter("updatedUtc", now),
                new ControlParameter("selectionId", row.SelectionId),
                new ControlParameter("sourceConnectionId", row.SourceConnectionId),
                new ControlParameter("targetConnectionId", row.TargetConnectionId)
            );
            transaction.Commit();
            return Task.FromResult(ToRecord(row));
        }
        if (existing.Version != ParseVersion(ifMatch))
            throw new PlanVersionMismatchException();
        var affected = db.Execute(
            "UPDATE Plans SET DisplayName = @displayName, OperatorNote = @operatorNote, SelectionId = @selectionId, SourceConnectionId = @sourceConnectionId, TargetConnectionId = @targetConnectionId, Version = Version + 1, CanonicalHash = NULL, SealFailureCode = NULL, SealFailureDetail = NULL, UpdatedUtc = @updatedUtc WHERE PlanId = @planId AND Version = @version",
            new ControlParameter("displayName", displayName),
            new ControlParameter("operatorNote", operatorNote),
            new ControlParameter("selectionId", selectionId?.ToString()),
            new ControlParameter("sourceConnectionId", sourceConnectionId?.ToString()),
            new ControlParameter("targetConnectionId", targetConnectionId?.ToString()),
            new ControlParameter("updatedUtc", now),
            new ControlParameter("planId", planId.ToString()),
            new ControlParameter("version", existing.Version)
        );
        if (affected != 1)
            throw new PlanVersionMismatchException();
        transaction.Commit();
        return Task.FromResult(
            new PlanRecord(
                planId,
                displayName,
                operatorNote,
                existing.Version + 1,
                null,
                clock.UtcNow,
                selectionId,
                sourceConnectionId,
                targetConnectionId
            )
        );
    }

    public Task<PlanRecord?> FindAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = Find(db, planId);
        return Task.FromResult(row is null ? null : ToRecord(row));
    }

    public Task SealAsync(Guid planId, TransferPlanContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var now = Stamp(clock.UtcNow);
        var affected = db.Execute(
            "UPDATE Plans SET ContentJson = @contentJson, SealedUtc = @sealedUtc, CanonicalHash = @canonicalHash, SealFailureCode = NULL, SealFailureDetail = NULL, UpdatedUtc = @updatedUtc WHERE PlanId = @planId",
            new ControlParameter("contentJson", JsonSerializer.Serialize(content)),
            new ControlParameter("sealedUtc", now),
            new ControlParameter("canonicalHash", CanonicalPlanHasher.Hash(content)),
            new ControlParameter("updatedUtc", now),
            new ControlParameter("planId", planId.ToString())
        );
        if (affected != 1)
            throw new InvalidOperationException("Plan was not found.");
        return Task.CompletedTask;
    }

    public Task RecordSealFailureAsync(Guid planId, string code, string detail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var affected = db.Execute(
            "UPDATE Plans SET SealFailureCode = @code, SealFailureDetail = @detail, UpdatedUtc = @updatedUtc WHERE PlanId = @planId",
            new ControlParameter("code", code),
            new ControlParameter("detail", detail),
            new ControlParameter("updatedUtc", Stamp(clock.UtcNow)),
            new ControlParameter("planId", planId.ToString())
        );
        if (affected != 1)
            throw new InvalidOperationException("Plan was not found.");
        return Task.CompletedTask;
    }

    public Task<TransferPlanContent?> LoadContentAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var contentJson = db.Scalar<string>(
            "SELECT ContentJson FROM Plans WHERE PlanId = @planId",
            new ControlParameter("planId", planId.ToString())
        );
        return Task.FromResult(
            contentJson is null ? null : JsonSerializer.Deserialize<TransferPlanContent>(contentJson)
        );
    }

    private static Row? Find(ControlConnection db, Guid planId) =>
        db.Single(SelectColumns + " WHERE PlanId = @planId", Map, new ControlParameter("planId", planId.ToString()));

    private static Row Map(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)
        );

    private static PlanRecord ToRecord(Row row) =>
        new(
            Guid.Parse(row.PlanId),
            row.DisplayName,
            row.OperatorNote,
            row.Version,
            row.CanonicalHash,
            DateTimeOffset.Parse(row.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            row.SelectionId is null ? null : Guid.Parse(row.SelectionId),
            row.SourceConnectionId is null ? null : Guid.Parse(row.SourceConnectionId),
            row.TargetConnectionId is null ? null : Guid.Parse(row.TargetConnectionId),
            row.SealFailureCode,
            row.SealFailureDetail
        );

    private static long ParseVersion(string ifMatch) =>
        long.TryParse(ifMatch.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
        && version > 0
            ? version
            : throw new PlanVersionMismatchException();

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private sealed record Row(
        string PlanId,
        string DisplayName,
        string? OperatorNote,
        long Version,
        string? CanonicalHash,
        string UpdatedUtc,
        string? SelectionId,
        string? SourceConnectionId,
        string? TargetConnectionId,
        string? SealFailureCode,
        string? SealFailureDetail
    );
}
