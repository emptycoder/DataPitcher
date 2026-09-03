using System.Globalization;
using System.Text.Json;
using DataPitcher.Core.Plans;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using LinqToDB;
using LinqToDB.Data;

namespace DataPitcher.Infrastructure.Plans;

public sealed record PlanRecord(Guid PlanId, string DisplayName, string? OperatorNote, long Version, string? CanonicalHash, DateTimeOffset UpdatedUtc, Guid? SelectionId = null, Guid? SourceConnectionId = null, Guid? TargetConnectionId = null);

public sealed class PlanVersionMismatchException : InvalidOperationException
{
    public PlanVersionMismatchException() : base("Plan version does not match.") { }
}

public sealed class PlanStore(ControlDatabase database, IClock clock)
{
    public Task<PlanRecord> SaveAsync(Guid planId, string displayName, string? operatorNote, string ifMatch, CancellationToken cancellationToken, Guid? selectionId = null, Guid? sourceConnectionId = null, Guid? targetConnectionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        var now = Stamp(clock.UtcNow);
        var existing = db.GetTable<PlanRow>().SingleOrDefault(row => row.PlanId == planId.ToString());
        if (existing is null)
        {
            var row = new PlanRow { PlanId = planId.ToString(), DisplayName = displayName, OperatorNote = operatorNote, Version = 1, CanonicalHash = null, CreatedUtc = now, UpdatedUtc = now, SelectionId = selectionId?.ToString(), SourceConnectionId = sourceConnectionId?.ToString(), TargetConnectionId = targetConnectionId?.ToString() };
            db.Insert(row);
            transaction.Commit();
            return Task.FromResult(ToRecord(row));
        }
        if (existing.Version != ParseVersion(ifMatch)) throw new PlanVersionMismatchException();
        var affected = db.Execute(
            "UPDATE Plans SET DisplayName = @displayName, OperatorNote = @operatorNote, SelectionId = @selectionId, SourceConnectionId = @sourceConnectionId, TargetConnectionId = @targetConnectionId, Version = Version + 1, CanonicalHash = NULL, UpdatedUtc = @updatedUtc WHERE PlanId = @planId AND Version = @version",
            new DataParameter("displayName", displayName), new DataParameter("operatorNote", operatorNote), new DataParameter("selectionId", selectionId?.ToString()), new DataParameter("sourceConnectionId", sourceConnectionId?.ToString()), new DataParameter("targetConnectionId", targetConnectionId?.ToString()), new DataParameter("updatedUtc", now),
            new DataParameter("planId", planId.ToString()), new DataParameter("version", existing.Version));
        if (affected != 1) throw new PlanVersionMismatchException();
        transaction.Commit();
        return Task.FromResult(new PlanRecord(planId, displayName, operatorNote, existing.Version + 1, null, clock.UtcNow, selectionId, sourceConnectionId, targetConnectionId));
    }

    public Task<PlanRecord?> FindAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var row = db.GetTable<PlanRow>().SingleOrDefault(row => row.PlanId == planId.ToString());
        return Task.FromResult(row is null ? null : ToRecord(row));
    }

    public Task SealAsync(Guid planId, TransferPlanContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var now = Stamp(clock.UtcNow);
        var affected = db.Execute("UPDATE Plans SET ContentJson = @contentJson, SealedUtc = @sealedUtc, CanonicalHash = @canonicalHash, UpdatedUtc = @updatedUtc WHERE PlanId = @planId", new DataParameter("contentJson", JsonSerializer.Serialize(content)), new DataParameter("sealedUtc", now), new DataParameter("canonicalHash", CanonicalPlanHasher.Hash(content)), new DataParameter("updatedUtc", now), new DataParameter("planId", planId.ToString()));
        if (affected != 1) throw new InvalidOperationException("Plan was not found.");
        return Task.CompletedTask;
    }

    public Task<TransferPlanContent?> LoadContentAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.Open();
        var contentJson = db.GetTable<PlanRow>().SingleOrDefault(row => row.PlanId == planId.ToString())?.ContentJson;
        return Task.FromResult(contentJson is null ? null : JsonSerializer.Deserialize<TransferPlanContent>(contentJson));
    }

    private static PlanRecord ToRecord(PlanRow row) =>
        new(Guid.Parse(row.PlanId), row.DisplayName, row.OperatorNote, row.Version, row.CanonicalHash, DateTimeOffset.Parse(row.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), row.SelectionId is null ? null : Guid.Parse(row.SelectionId), row.SourceConnectionId is null ? null : Guid.Parse(row.SourceConnectionId), row.TargetConnectionId is null ? null : Guid.Parse(row.TargetConnectionId));

    private static long ParseVersion(string ifMatch) =>
        long.TryParse(ifMatch.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw new PlanVersionMismatchException();

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
