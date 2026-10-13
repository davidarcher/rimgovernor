namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent for one pawn to drop the weapon it holds. The pawn is not drafted; native eligibility and the
/// weapon being in the pawn's hands are established at inspection, not here.
/// </summary>
public sealed record DropEquipment : IActionPayload
{
    /// <summary>Go <c>NewDropEquipment</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public DropEquipment(PawnId pawn, string thing)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(thing) && thing != pawn.Value,
            "drop equipment requires a valid pawn and a distinct held weapon"
        );
        Pawn = pawn;
        Thing = thing;
    }

    public PawnId Pawn { get; }

    public string Thing { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewDropEquipmentAction(ActionId id, DropEquipment drop) => Create(id, ActionKind.DropEquipment, drop);

    public DropEquipment? DropEquipment => PayloadOf<DropEquipment>(ActionKind.DropEquipment);
}
