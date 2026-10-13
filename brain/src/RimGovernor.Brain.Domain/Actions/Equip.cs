namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to arm one already-observed unarmed pawn with one already-observed loose weapon. The pawn is
/// not drafted; native reachability, current eligibility and job availability are established at inspection,
/// not here. Cell is carried the same way Haul/SupplyAllow carry it: a fresh CAS token can only be re-queried
/// scoped to a cell, not by thing ID alone.
/// </summary>
public sealed record Equip : IActionPayload
{
    /// <summary>Go <c>NewEquip</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Equip(PawnId pawn, string thing, string definition, Cell cell)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(thing) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "equip requires a valid pawn, thing, definition identity and cell"
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
    public static DomainAction NewEquipAction(ActionId id, Equip equip) => Create(id, ActionKind.Equip, equip);

    public Equip? Equip => PayloadOf<Equip>(ActionKind.Equip);
}
