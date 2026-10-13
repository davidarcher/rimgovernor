namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to beat a colonist in an aggressive mental break down (a GiveJobIntent AttackMelee). It
/// rides a preceding owned draft of the same pawn, which keeps the subduer drafted while the plan lives; it
/// proves nothing about the target's eligibility or reachability.
/// </summary>
public sealed record Subdue : IActionPayload
{
    public Subdue(PawnId pawn, PawnId target, ActionId prerequisite)
    {
        DomainException.Require(pawn.IsValid && target.IsValid && pawn != target, "subdue requires distinct valid pawn and target identities");
        DomainException.Require(prerequisite.IsValid, "invalid subdue draft prerequisite");
        Pawn = pawn;
        Target = target;
        DraftAction = prerequisite;
    }

    public PawnId Pawn { get; }

    public PawnId Target { get; }

    public ActionId DraftAction { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewSubdueAction(ActionId id, Subdue subdue)
    {
        DomainException.Require(id != subdue.DraftAction, "invalid subdue action identity or self prerequisite");
        return Create(id, ActionKind.Subdue, subdue);
    }

    public Subdue? Subdue => PayloadOf<Subdue>(ActionKind.Subdue);
}
