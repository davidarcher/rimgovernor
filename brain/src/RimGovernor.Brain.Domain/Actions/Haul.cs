namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to move one already-observed loose thing. The pawn is not drafted; native storage search
/// picks the destination, and current eligibility and job availability are established at inspection, not
/// here. Cell is carried the same way SupplyAllow carries it: a fresh CAS token can only be re-queried scoped
/// to a cell, not by thing ID alone.
/// </summary>
public sealed record Haul : IActionPayload
{
    /// <summary>Go <c>NewHaul</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Haul(PawnId pawn, string thing, string definition, Cell cell)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "haul requires a valid pawn, thing, definition identity and cell"
        );
        Pawn = pawn;
        Thing = thing;
        Definition = definition;
        Cell = cell;
    }

    public PawnId Pawn { get; }

    public string Thing { get; }

    public string Definition { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewHaulAction(ActionId id, Haul haul) => Create(id, ActionKind.Haul, haul);

    /// <summary>Go <c>Action.Haul() (Haul, bool)</c>: the payload when this is a haul action, otherwise null.</summary>
    public Haul? Haul => PayloadOf<Haul>(ActionKind.Haul);
}
