namespace RimGovernor.Brain.Domain;

/// <summary>Releases a drafted pawn no live plan needs, through the ordinary action journal.</summary>
public sealed record Undraft : IActionPayload
{
    public Undraft(PawnId pawn)
    {
        DomainException.Require(pawn.IsValid, "invalid undraft pawn");
        Pawn = pawn;
    }

    public PawnId Pawn { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewUndraftAction(ActionId id, Undraft undraft) => Create(id, ActionKind.Undraft, undraft);
    public Undraft? Undraft => PayloadOf<Undraft>(ActionKind.Undraft);
}
