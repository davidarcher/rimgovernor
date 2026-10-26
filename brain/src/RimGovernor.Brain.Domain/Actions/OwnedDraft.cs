namespace RimGovernor.Brain.Domain;

/// <summary>
/// Drafts one pawn for the plan that holds it (a DraftIntent with drafted=true). The draft is the plan's: the
/// restore-workers family admits a separate undraft action once no live plan needs the pawn, so there is
/// no native claim or implicit cleanup attached to this action.
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
