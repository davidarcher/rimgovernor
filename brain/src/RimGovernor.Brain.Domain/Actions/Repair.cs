namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to have one pawn repair one damaged, player-owned structure. The pawn is not drafted; native
/// eligibility, current job state and the structure's remaining damage are established at inspection, not here.
/// Cell is carried so dispatch can re-scope a fresh CAS snapshot token for the structure, mirroring Haul's Cell
/// field: the general upkeep census deliberately strips snapshot tokens from its rows.
/// </summary>
public sealed record Repair : IActionPayload
{
    /// <summary>Go <c>NewRepair</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Repair(PawnId pawn, string structure, Cell cell)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(structure) && cell.X >= 0 && cell.Z >= 0,
            "repair requires a valid pawn, structure and nonnegative cell"
        );
        Pawn = pawn;
        Structure = structure;
        Cell = cell;
    }

    public PawnId Pawn { get; }

    public string Structure { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRepairAction(ActionId id, Repair repair) => Create(id, ActionKind.Repair, repair);

    public Repair? Repair => PayloadOf<Repair>(ActionKind.Repair);
}
