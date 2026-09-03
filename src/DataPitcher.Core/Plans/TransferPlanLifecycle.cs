namespace DataPitcher.Core.Plans;

public sealed class TransferPlanDraft
{
    public TransferPlanDraft(
        string displayName,
        string? operatorNote,
        string createdBy,
        DateTimeOffset createdAtUtc,
        TransferPlanContent content
    )
    {
        DisplayName = displayName;
        OperatorNote = operatorNote;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        Content = content;
    }

    public string DisplayName { get; }
    public string? OperatorNote { get; }
    public string CreatedBy { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public TransferPlanContent Content { get; }
}

public sealed record TransferPlanIdentity(
    Guid PlanId,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset SealedAtUtc,
    string CreatedBy
);

public sealed class SealedTransferPlan
{
    public SealedTransferPlan(TransferPlanIdentity identity, TransferPlanContent content, string canonicalHash)
    {
        Identity = identity;
        Content = content;
        CanonicalHash = canonicalHash;
    }

    public TransferPlanIdentity Identity { get; }
    public TransferPlanContent Content { get; }
    public string CanonicalHash { get; }
}

public sealed class TransferPlanLifecycle
{
    private int _nextVersion = 1;

    public TransferPlanLifecycle(TransferPlanDraft draft) => Draft = draft;

    public TransferPlanDraft Draft { get; private set; }
    public SealedTransferPlan? CurrentSeal { get; private set; }

    public void Replace(TransferPlanDraft draft)
    {
        var changed = !StringComparer.Ordinal.Equals(
            CanonicalPlanHasher.Hash(Draft.Content),
            CanonicalPlanHasher.Hash(draft.Content)
        );
        Draft = draft;
        if (changed && CurrentSeal is not null)
        {
            CurrentSeal = null;
            _nextVersion++;
        }
    }

    public SealedTransferPlan Seal(Guid planId, DateTimeOffset sealedAtUtc)
    {
        CurrentSeal ??= new SealedTransferPlan(
            new(planId, _nextVersion, Draft.CreatedAtUtc, sealedAtUtc, Draft.CreatedBy),
            Draft.Content,
            CanonicalPlanHasher.Hash(Draft.Content)
        );
        return CurrentSeal;
    }
}
