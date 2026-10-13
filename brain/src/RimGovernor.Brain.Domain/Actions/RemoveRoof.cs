namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates vanilla RemoveRoof over cells (a RemoveRoofIntent on Actions/Apply). Clearance issues it over
/// an enclosed room's roofed cells before deconstructing the walls holding that roof. Applied means
/// designated; the roof read decides when pawns finished. Canonical cells (sorted, deduplicated), at least one.
/// </summary>
public sealed record RemoveRoof : IActionPayload
{
    /// <summary>Go <c>NewRemoveRoof</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public RemoveRoof(IReadOnlyCollection<Cell> cells)
    {
        DomainException.Require(cells.Count != 0, "remove roof needs cells");
        Cells = CanonicalCells.Sort(cells, "invalid remove roof cell");
    }

    public EquatableArray<Cell> Cells { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRemoveRoofAction(ActionId id, RemoveRoof roof) => Create(id, ActionKind.RemoveRoof, roof);

    /// <summary>Go <c>Action.RemoveRoof() (RemoveRoof, bool)</c>: the payload when this is a remove roof action, otherwise null.</summary>
    public RemoveRoof? RemoveRoof => PayloadOf<RemoveRoof>(ActionKind.RemoveRoof);
}
