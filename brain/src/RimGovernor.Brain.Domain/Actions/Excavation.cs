namespace RimGovernor.Brain.Domain;

/// <summary>
/// Designates one rock cell for ordinary pawn mining as one step of a staged room/corridor excavation. Unlike
/// a mine acquisition the target is a cell and its expected rock definition, never a Mineable thing identity
/// (native compressed rocks are recreated with fresh IDs on load), and completion is the cell being cleared,
/// not any output yield. Roof support, worker access and mining legality are native reads rechecked at
/// dispatch and before each pick hit, not properties of this intent.
/// </summary>
public sealed record Excavation : IActionPayload
{
    /// <summary>Go <c>NewExcavation</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Excavation(Cell cell, string definition)
    {
        DomainException.Require(Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0, "invalid excavation definition or cell");
        Cell = cell;
        Definition = definition;
    }

    public Cell Cell { get; }

    public string Definition { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewExcavationAction(ActionId id, Excavation excavation) => Create(id, ActionKind.Excavation, excavation);

    /// <summary>Go <c>Action.Excavation() (Excavation, bool)</c>: the payload when this is an excavation action, otherwise null.</summary>
    public Excavation? Excavation => PayloadOf<Excavation>(ActionKind.Excavation);
}
