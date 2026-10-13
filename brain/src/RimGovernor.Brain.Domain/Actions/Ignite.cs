namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to have one pawn throw a molotov at one cell: the pawn is drafted, carries the molotov and
/// force-fires it at the cell. Native refuses while any pawn stands in the target cell's room; this does not
/// restate that. Applied means the throw order was taken; whether the fire caught is a separate observed state.
/// </summary>
public sealed record Ignite : IActionPayload
{
    /// <summary>Go <c>NewIgnite</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Ignite(PawnId pawn, Cell cell)
    {
        DomainException.Require(pawn.IsValid && cell.X >= 0 && cell.Z >= 0, "ignite requires a valid pawn and cell");
        Pawn = pawn;
        Cell = cell;
    }

    public PawnId Pawn { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewIgniteAction(ActionId id, Ignite ignite) => Create(id, ActionKind.Ignite, ignite);

    public Ignite? Ignite => PayloadOf<Ignite>(ActionKind.Ignite);
}
