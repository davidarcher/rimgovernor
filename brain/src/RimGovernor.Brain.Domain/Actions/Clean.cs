namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to have one pawn clean one specific filth entity. The pawn is not drafted; native
/// eligibility, current job state and whether the filth still exists are established at inspection, not here.
/// Cell is carried so dispatch can re-scope a fresh CAS snapshot token for the filth, mirroring Repair's Cell
/// field: filth has no exact-ID lookup RPC, so its token must be resolved by scanning the cell's things instead.
/// </summary>
public sealed record Clean : IActionPayload
{
    /// <summary>Go <c>NewClean</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Clean(PawnId pawn, string filth, Cell cell)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(filth) && cell.X >= 0 && cell.Z >= 0,
            "clean requires a valid pawn, filth and nonnegative cell"
        );
        Pawn = pawn;
        Filth = filth;
        Cell = cell;
    }

    public PawnId Pawn { get; }

    public string Filth { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCleanAction(ActionId id, Clean clean) => Create(id, ActionKind.Clean, clean);

    public Clean? Clean => PayloadOf<Clean>(ActionKind.Clean);
}
