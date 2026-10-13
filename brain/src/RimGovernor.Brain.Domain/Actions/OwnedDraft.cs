namespace RimGovernor.Brain.Domain;

/// <summary>
/// Drafts one pawn for the plan that holds it (a DraftIntent with drafted=true). The draft is the plan's: the
/// combat planner undrafts any drafted colonist no live plan needs, so there is no native claim and no
/// per-action cleanup.
/// </summary>
public sealed record OwnedDraft : IActionPayload
{
    public OwnedDraft(PawnId pawn)
    {
        DomainException.Require(pawn.IsValid, "invalid draft pawn");
        Pawn = pawn;
    }

    public PawnId Pawn { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewOwnedDraftAction(ActionId id, OwnedDraft draft) => Create(id, ActionKind.OwnedDraft, draft);

    public OwnedDraft? OwnedDraft => PayloadOf<OwnedDraft>(ActionKind.OwnedDraft);
}
